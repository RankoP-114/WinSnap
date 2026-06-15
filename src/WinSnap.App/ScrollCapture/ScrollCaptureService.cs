using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using WinSnap.Core.Primitives;
using WinSnap.Core.Stitching;
using WinSnap.Interop;

namespace WinSnap.App.ScrollCapture;

/// <summary>
/// 长截图（滚动截图）服务：在给定的物理像素矩形内逐帧捕获，驱动（或等待）目标区域滚动，
/// 用 <see cref="ScrollStitcher"/> 把各帧增量拼成一张长图，最终返回 WPF <see cref="BitmapSource"/>。
///
/// 自动模式每帧后用 <see cref="InputSimulator.ScrollVertical"/> 注入向下滚轮，等待一小段时间让目标重绘，
/// 再截下一帧；直到 <see cref="ScrollStitcher.IsAtBottom"/> 或触达上限。
/// 线程：滚动/拼接为 CPU 工作，放在线程池执行；GDI 抓帧经 UI Dispatcher 执行，避开部分
/// RDP/终端服务环境中后台线程 BitBlt 偶发黑帧的问题。返回的 <see cref="BitmapSource"/> 已 Freeze，可跨线程使用。
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

    /// <summary>
    /// 执行一次长截图。
    /// </summary>
    /// <param name="regionX">目标矩形左上角 X（虚拟桌面物理像素坐标）。</param>
    /// <param name="regionY">目标矩形左上角 Y（虚拟桌面物理像素坐标）。</param>
    /// <param name="regionW">目标矩形宽（物理像素）。</param>
    /// <param name="regionH">目标矩形高（物理像素）。</param>
    /// <param name="ct">取消令牌：取消时若已有 ≥1 帧则返回当前拼接结果，否则返回 null。</param>
    /// <returns>拼接好的长图（已 Freeze）；无任何有效帧时为 null。</returns>
    public async Task<BitmapSource?> CaptureAsync(
        int regionX, int regionY, int regionW, int regionH,
        CancellationToken ct)
    {
        if (regionW <= 0 || regionH <= 0)
            return null;

        // 把光标放到区域中心，确保自动模式下滚轮命中目标、并尽量减小光标对截图内容的干扰。
        int centerX = regionX + regionW / 2;
        int centerY = regionY + regionH / 2;

        using var stitcher = new ScrollStitcher();
        var captureDispatcher = Application.Current?.Dispatcher;

        try
        {
            await Task.Run(async () =>
            {
                InputSimulator.MoveCursorTo(centerX, centerY);

                for (int frameIndex = 0; frameIndex < MaxFrames; frameIndex++)
                {
                    ct.ThrowIfCancellationRequested();

                    // 每帧重置光标到中心：避免用户/系统期间移走光标导致滚轮丢失目标。
                    InputSimulator.MoveCursorTo(centerX, centerY);

                    var captured = CaptureRegionOnDispatcher(
                        captureDispatcher,
                        regionX,
                        regionY,
                        regionW,
                        regionH);
                    var frame = ToPixelBuffer(captured);
                    stitcher.Append(frame, ownsFrame: true);

                    if (stitcher.LastMatchFailed)
                    {
                        Log.Warning(
                            "长截图停止：第 {FrameIndex} 帧重叠匹配失败，平均 SAD={Sad:F2}",
                            frameIndex + 1,
                            stitcher.LastMatchAverageSadPerChannel);
                        break;
                    }
                    if (stitcher.IsAtBottom)
                        break;
                    if (stitcher.CurrentHeight >= MaxStitchedHeight)
                        break;

                    InputSimulator.ScrollVertical(-WheelNotchesPerStep * InputSimulator.WheelDelta);
                    await Task.Delay(AutoScrollDelayMs, ct).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消：已有内容则尽量返回，否则向上层返回 null。
        }
        if (stitcher.CurrentHeight <= 0 || stitcher.Width <= 0)
            return null;

        return ToBitmapSource(stitcher.BuildAndReset());
    }

    private static CapturedImage CaptureRegionOnDispatcher(
        Dispatcher? dispatcher,
        int x,
        int y,
        int width,
        int height)
    {
        if (dispatcher is null ||
            dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished ||
            dispatcher.CheckAccess())
        {
            return GdiCapture.CaptureRegion(x, y, width, height);
        }

        return dispatcher.Invoke(
            () => GdiCapture.CaptureRegion(x, y, width, height),
            DispatcherPriority.Send);
    }

    // ---- 转换辅助：与现有 ImagingHelper 风格一致。----

    /// <summary>把 GDI 捕获的 BGRA 帧（top-down, stride=Width*4）包装为平台无关的 <see cref="PixelBuffer"/>。</summary>
    private static PixelBuffer ToPixelBuffer(CapturedImage img)
    {
        // CapturedImage 通常已是紧密排布的 32bpp BGRA、stride=Width*4，与 PixelBuffer 约定一致。
        // 调用方不会再复用该 CapturedImage，因此正常路径直接转交数组所有权。
        byte[] src = img.PixelsBgra;
        int expected = img.Width * img.Height * PixelBuffer.BytesPerPixel;
        byte[] bgra;
        if (src.Length == expected)
        {
            bgra = src;
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
