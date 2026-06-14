using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSnap.Core.Primitives;
using WinSnap.Core.Stitching;
using WinSnap.Interop;

namespace WinSnap.App.ScrollCapture;

/// <summary>
/// 长截图（滚动截图）服务：在给定的物理像素矩形内逐帧捕获，驱动（或等待）目标区域滚动，
/// 用 <see cref="ScrollStitcher"/> 把各帧增量拼成一张长图，最终返回 WPF <see cref="BitmapSource"/>。
///
/// 两种工作模式：
/// - 自动（<c>autoScroll = true</c>）：每帧后用 <see cref="InputSimulator.ScrollVertical"/> 注入向下滚轮，
///   等待一小段时间让目标重绘，再截下一帧；直到 <see cref="ScrollStitcher.IsAtBottom"/> 或触达上限。
/// - 半自动（<c>autoScroll = false</c>）：服务截首帧后等待外部信号，由用户手动滚动后调用
///   <see cref="AppendManualFrame"/> 触发下一帧；调用 <see cref="FinishManual"/> 结束。
///
/// 线程：捕获/拼接为 CPU 工作，放在线程池执行；返回的 <see cref="BitmapSource"/> 已 Freeze，可跨线程使用。
/// </summary>
public sealed class ScrollCaptureService
{
    /// <summary>安全上限：最多追加的帧数，防止目标“无限滚动”时卡死。</summary>
    public int MaxFrames { get; init; } = 200;

    /// <summary>安全上限：拼接结果最大高度（像素）。</summary>
    public int MaxStitchedHeight { get; init; } = 60000;

    /// <summary>自动模式下每次注入的滚轮刻度数（向下，取正；内部取负发送）。</summary>
    public int WheelNotchesPerStep { get; init; } = 3;

    /// <summary>自动模式下每帧之间的等待（毫秒），给目标留出重绘时间。</summary>
    public int AutoScrollDelayMs { get; init; } = 120;

    // 半自动：每次外部调用 AppendManualFrame 都唤醒采集循环抓取下一帧。
    private TaskCompletionSource<bool>? _manualSignal;
    private readonly object _manualLock = new();

    /// <summary>
    /// 执行一次长截图。
    /// </summary>
    /// <param name="regionX">目标矩形左上角 X（虚拟桌面物理像素坐标）。</param>
    /// <param name="regionY">目标矩形左上角 Y（虚拟桌面物理像素坐标）。</param>
    /// <param name="regionW">目标矩形宽（物理像素）。</param>
    /// <param name="regionH">目标矩形高（物理像素）。</param>
    /// <param name="autoScroll">true=自动注入滚轮；false=半自动，等待 <see cref="AppendManualFrame"/>。</param>
    /// <param name="ct">取消令牌：取消时若已有 ≥1 帧则返回当前拼接结果，否则返回 null。</param>
    /// <returns>拼接好的长图（已 Freeze）；无任何有效帧时为 null。</returns>
    public async Task<BitmapSource?> CaptureAsync(
        int regionX, int regionY, int regionW, int regionH,
        bool autoScroll, CancellationToken ct)
    {
        if (regionW <= 0 || regionH <= 0)
            return null;

        // 把光标放到区域中心，确保自动模式下滚轮命中目标、并尽量减小光标对截图内容的干扰。
        int centerX = regionX + regionW / 2;
        int centerY = regionY + regionH / 2;

        var stitcher = new ScrollStitcher();

        // 每步滚动后内容上移约一帧高度的一部分（保留足够竖直重叠供模板匹配）。
        int scrollOverlap = Math.Max(1, regionH / 4);

        try
        {
            await Task.Run(async () =>
            {
                InputSimulator.MoveCursorTo(centerX, centerY);

                for (int frameIndex = 0; frameIndex < MaxFrames; frameIndex++)
                {
                    ct.ThrowIfCancellationRequested();

                    // 自动模式下每帧重置光标到中心：避免用户/系统期间移走光标导致滚轮丢失目标。
                    if (autoScroll)
                        InputSimulator.MoveCursorTo(centerX, centerY);

                    var captured = GdiCapture.CaptureRegion(regionX, regionY, regionW, regionH);
                    var frame = ToPixelBuffer(captured);
                    stitcher.Append(frame);

                    if (stitcher.IsAtBottom)
                        break;
                    if (stitcher.CurrentHeight >= MaxStitchedHeight)
                        break;

                    if (autoScroll)
                    {
                        // delta<0 向下滚；幅度按重叠目标折算成滚轮刻度。
                        InputSimulator.ScrollVertical(-WheelNotchesPerStep * InputSimulator.WheelDelta);
                        await Task.Delay(AutoScrollDelayMs, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // 半自动：等待外部信号（用户已手动滚动并请求追加下一帧）。
                        bool more = await WaitForManualSignalAsync(ct).ConfigureAwait(false);
                        if (!more)
                            break; // 外部调用 FinishManual：结束采集
                    }
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消：已有内容则尽量返回，否则向上层返回 null。
        }
        finally
        {
            CompleteManualSignal(false);
        }

        _ = scrollOverlap; // 重叠量由 ScrollStitcher 自行通过模板匹配确定，这里仅作语义说明。

        if (stitcher.CurrentHeight <= 0 || stitcher.Width <= 0)
            return null;

        return ToBitmapSource(stitcher.Build());
    }

    /// <summary>
    /// 半自动模式下由外部（如按下“下一帧”热键）调用：请求采集循环再抓取并拼接一帧。
    /// 自动模式或当前无进行中的等待时调用无副作用。
    /// </summary>
    public void AppendManualFrame() => CompleteManualSignal(true);

    /// <summary>
    /// 半自动模式下由外部调用以结束采集（已到底或用户主动完成）。
    /// 采集循环将退出并返回当前拼接结果。
    /// </summary>
    public void FinishManual() => CompleteManualSignal(false);

    private Task<bool> WaitForManualSignalAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool> tcs;
        lock (_manualLock)
        {
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _manualSignal = tcs;
        }

        // 取消时让等待任务以取消收场（由 Task.Delay/外层 catch 统一处理）。
        var reg = ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), tcs);
        return AwaitAndUnregister(tcs, reg);

        static async Task<bool> AwaitAndUnregister(TaskCompletionSource<bool> t, CancellationTokenRegistration r)
        {
            try { return await t.Task.ConfigureAwait(false); }
            finally { r.Dispose(); }
        }
    }

    private void CompleteManualSignal(bool more)
    {
        lock (_manualLock)
        {
            _manualSignal?.TrySetResult(more);
            _manualSignal = null;
        }
    }

    // ---- 转换辅助：与现有 ImagingHelper 风格一致，但自包含以满足“只新增不改现有文件”。----

    /// <summary>把 GDI 捕获的 BGRA 帧（top-down, stride=Width*4）包装为平台无关的 <see cref="PixelBuffer"/>。</summary>
    private static PixelBuffer ToPixelBuffer(CapturedImage img)
    {
        // CapturedImage 已是紧密排布的 32bpp BGRA、stride=Width*4，与 PixelBuffer 约定一致。
        // 复制一份所有权交给 PixelBuffer（其构造要求长度恰为 W*H*4）。
        byte[] src = img.PixelsBgra;
        int expected = img.Width * img.Height * PixelBuffer.BytesPerPixel;
        byte[] bgra;
        if (src.Length == expected)
        {
            bgra = (byte[])src.Clone();
        }
        else
        {
            // 防御：源含行填充时逐行紧凑复制（正常路径不会走到）。
            bgra = new byte[expected];
            int dstStride = img.Width * PixelBuffer.BytesPerPixel;
            int copy = Math.Min(dstStride, img.Stride);
            for (int row = 0; row < img.Height; row++)
                Buffer.BlockCopy(src, row * img.Stride, bgra, row * dstStride, copy);
        }
        return new PixelBuffer(img.Width, img.Height, bgra);
    }

    /// <summary>把拼接结果 <see cref="PixelBuffer"/> 转为可冻结的 WPF 位图（Bgra32, 96dpi）。</summary>
    private static BitmapSource ToBitmapSource(PixelBuffer buffer)
    {
        var bmp = BitmapSource.Create(
            buffer.Width, buffer.Height,
            96, 96,
            PixelFormats.Bgra32, null,
            buffer.Bgra, buffer.Stride);
        bmp.Freeze(); // 可跨线程、渲染更高效
        return bmp;
    }
}
