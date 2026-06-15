using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinSnap.Core.Imaging;

namespace WinSnap.Interop;

/// <summary>
/// 基于 DXGI Desktop Duplication（<see cref="IDXGIOutput5.DuplicateOutput1"/> +
/// <c>R16G16B16A16_FLOAT</c> scRGB）的 HDR 感知屏幕捕获。
///
/// <para>
/// 相比 <see cref="GdiCapture"/>（GDI BitBlt，在 HDR 屏会过曝泛白），本路径以 FP16 scRGB
/// 抓取每块显示器的线性光，再经 <see cref="ToneMapper"/> 做 BT.2390 风格色调映射 → SDR sRGB BGRA8，
/// 故 HDR 屏内容能正确压回 SDR 不过曝。每块屏按其 <c>DesktopCoordinates</c> 拼进一张覆盖整个虚拟桌面的
/// <see cref="CapturedImage"/>。
/// </para>
///
/// <para>
/// scRGB 语义：线性 FP16，(1,1,1) == D65 白 80 nits，可超 1.0 表 HDR 高光，可为负表广色域。
/// 原色为 Rec.709，故 tone map 时传 <c>inputIsRec2020:false</c> 跳过色域矩阵。
/// </para>
///
/// <para>
/// 健壮性：单块屏失败（超时无新帧 / 受保护内容 / 不支持 Duplication）只影响该屏（填黑或跳过并记录），
/// 不影响其余屏，绝不整体抛出。Duplication 要求至少一帧画面有更新才会产帧；首帧可能超时，故内部对每块屏做
/// 有限次重试。
/// </para>
/// </summary>
public static class DuplicationCapture
{
    private const uint DxgiErrorAccessLost = 0x887A0026;

    /// <summary>每块屏 AcquireNextFrame 的单次超时（毫秒）。</summary>
    private const int AcquireTimeoutMs = 120;

    /// <summary>
    /// 每块屏获取「有效帧」的最大尝试次数。Desktop Duplication 的首帧 <c>desktop image</c> 常未填充
    /// （<c>LastPresentTime==0</c> 的占位/仅元数据帧），必须丢弃并重试到一帧真正有画面呈现；
    /// 加之静止桌面要等下一次刷新（光标、时钟等），故预算给得较宽。
    /// </summary>
    private const int AcquireMaxAttempts = 40;

    /// <summary>整块屏获取有效帧的总时间预算（毫秒），超时即放弃该屏。</summary>
    private const int AcquireTotalBudgetMs = 1500;

    [ThreadStatic]
    private static IReadOnlyList<string>? _lastDiagnostics;

    /// <summary>当前线程最近一次捕获的诊断日志（捕获过程中各屏的降级原因）；调用方可选读。</summary>
    public static IReadOnlyList<string> LastDiagnostics => _lastDiagnostics ?? Array.Empty<string>();

    /// <summary>
    /// 抓取整个虚拟桌面，对 HDR 屏做 scRGB→SDR tone map，对 SDR 屏近似原样还原。
    /// 返回尺寸/原点 == <see cref="VirtualScreenInfo.Get"/> 的虚拟桌面，top-down BGRA8。
    /// 任意失败均优雅降级：失败的屏保留 GDI 基底；若 GDI 基底也不可用则保持黑色并记录诊断。
    /// </summary>
    public static CapturedImage CaptureVirtualScreenHdrAware(
        bool captureSdrWithDesktopDuplication = false,
        int sdrAcquireTotalBudgetMs = 360,
        double hdrSdrWhiteNits = ToneMapper.DefaultSdrWhiteNits,
        double hdrPeakNits = ToneMapper.DefaultHdrPeakNits)
    {
        var diag = new List<string>();
        var vs = VirtualScreenInfo.Get();

        int canvasW = Math.Max(vs.Width, 0);
        int canvasH = Math.Max(vs.Height, 0);

        if (canvasW == 0 || canvasH == 0)
        {
            SetLastDiagnostics(diag);
            return new CapturedImage(canvasW, canvasH, Array.Empty<byte>());
        }

        // 目标画布：先用 GDI 抓整个虚拟桌面作基底，而非初始全黑——这是杜绝「HDR 截图全屏黑」的关键。
        //  - SDR 屏：GDI 即完美最终内容，下方直接跳过 Desktop Duplication（不受静止桌面 AcquireNextFrame
        //    超时、DuplicateOutput 并发上限等影响）。
        //  - HDR 屏：GDI 为过曝（泛白）内容，下方用 DD 抓 FP16 scRGB → tone map 覆盖为正确 SDR；
        //    若该屏 DD 失败，至少保留 GDI 过曝内容而非整块全黑。
        // GDI 同步即时、不依赖画面更新，是可靠兜底基底。
        byte[] canvas = BuildGdiBaseCanvas(canvasW, canvasH, diag);
        bool gdiBaseHasVisibleContent = CapturedImage.HasVisibleContent(canvasW, canvasH, canvas);
        if (!gdiBaseHasVisibleContent)
            diag.Add("GDI 基底看起来全黑：将对 SDR 屏也尝试 Desktop Duplication。");

        bool enumerated = ForEachOutput(
            diag,
            factoryFailureMessage: "CreateDXGIFactory1 失败：返回全黑画布。",
            topLevelFailurePrefix: "顶层异常",
            outputFailureMessage: static (adapterIndex, outputIndex, ex) =>
                $"adapter#{adapterIndex} output#{outputIndex} 异常：{ex.GetType().Name} {ex.Message}（保留当前基底）。",
            handleOutput: (device, context, output, adapterIndex, outputIndex) =>
                CaptureOneOutput(device, context, output, vs, canvas, canvasW, canvasH,
                    gdiBaseHasVisibleContent, captureSdrWithDesktopDuplication,
                    sdrAcquireTotalBudgetMs, hdrSdrWhiteNits, hdrPeakNits,
                    diag, adapterIndex, outputIndex));
        if (!enumerated)
        {
            SetLastDiagnostics(diag);
            return new CapturedImage(canvasW, canvasH, canvas);
        }

        SetLastDiagnostics(diag);
        return new CapturedImage(canvasW, canvasH, canvas);
    }

    /// <summary>
    /// 抓取虚拟桌面坐标系中的指定区域。相比 <see cref="CaptureVirtualScreenHdrAware"/>，本方法只为
    /// 目标区域分配 BGRA 画布，并且 Desktop Duplication 帧只复制与目标区域相交的子矩形，适合 GIF 连续抓帧。
    /// </summary>
    public static CapturedImage CaptureRegionHdrAware(
        int x,
        int y,
        int width,
        int height,
        bool captureSdrWithDesktopDuplication = false,
        int sdrAcquireTotalBudgetMs = 360,
        double hdrSdrWhiteNits = ToneMapper.DefaultSdrWhiteNits,
        double hdrPeakNits = ToneMapper.DefaultHdrPeakNits)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var diag = new List<string>();
        var vs = VirtualScreenInfo.Get();
        if (x < vs.X || y < vs.Y || x + width > vs.Right || y + height > vs.Bottom)
            throw new InvalidOperationException("HDR 区域抓取范围已超出当前虚拟桌面。");

        byte[] canvas = BuildGdiRegionCanvas(x, y, width, height, diag);
        bool gdiBaseHasVisibleContent = CapturedImage.HasVisibleContent(width, height, canvas);
        if (!gdiBaseHasVisibleContent)
            diag.Add("GDI 区域基底看起来全黑：将对 SDR 屏也尝试 Desktop Duplication。");

        bool enumerated = ForEachOutput(
            diag,
            factoryFailureMessage: "CreateDXGIFactory1 失败：返回 GDI 区域基底。",
            topLevelFailurePrefix: "区域顶层异常",
            outputFailureMessage: static (adapterIndex, outputIndex, ex) =>
                $"adapter#{adapterIndex} output#{outputIndex} 区域异常：{ex.GetType().Name} {ex.Message}（保留当前基底）。",
            handleOutput: (device, context, output, adapterIndex, outputIndex) =>
                CaptureOneOutputRegion(device, context, output,
                    x, y, width, height, canvas,
                    gdiBaseHasVisibleContent, captureSdrWithDesktopDuplication,
                    sdrAcquireTotalBudgetMs, hdrSdrWhiteNits, hdrPeakNits,
                    diag, adapterIndex, outputIndex));
        if (!enumerated)
        {
            SetLastDiagnostics(diag);
            return new CapturedImage(width, height, canvas);
        }

        SetLastDiagnostics(diag);
        return new CapturedImage(width, height, canvas);
    }

    /// <summary>
    /// 为连续区域抓帧创建可复用的 Desktop Duplication 会话。适合 GIF 录制这类重复抓取同一区域的场景；
    /// GDI 基底仍然逐帧刷新，DD 只复用昂贵的 factory/device/duplication 资源。
    /// </summary>
    public static RegionCaptureSession CreateRegionCaptureSession(
        int x,
        int y,
        int width,
        int height,
        bool captureSdrWithDesktopDuplication = false,
        int sdrAcquireTotalBudgetMs = 360,
        double hdrSdrWhiteNits = ToneMapper.DefaultSdrWhiteNits,
        double hdrPeakNits = ToneMapper.DefaultHdrPeakNits)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var vs = VirtualScreenInfo.Get();
        if (x < vs.X || y < vs.Y || x + width > vs.Right || y + height > vs.Bottom)
            throw new InvalidOperationException("HDR 区域抓取范围已超出当前虚拟桌面。");

        var diagnostics = new List<string>();
        var outputs = new List<RegionOutputCapture>();
        IDXGIFactory1? factory = null;
        try
        {
            if (DXGI.CreateDXGIFactory1(out factory).Failure || factory is null)
            {
                diagnostics.Add("CreateDXGIFactory1 失败：区域连续抓帧会话仅使用 GDI 基底。");
                return new RegionCaptureSession(
                    x, y, width, height,
                    captureSdrWithDesktopDuplication,
                    sdrAcquireTotalBudgetMs,
                    hdrSdrWhiteNits,
                    hdrPeakNits,
                    outputs,
                    diagnostics);
            }

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Failure || adapter is null)
                    break;

                try
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        if (adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Failure || output is null)
                            break;

                        bool ownershipTransferred = false;
                        IDXGIOutput6? output6 = null;
                        ID3D11Device? device = null;
                        ID3D11DeviceContext? context = null;
                        try
                        {
                            output6 = output.QueryInterface<IDXGIOutput6>();
                            OutputDescription1 desc1 = output6.Description1;
                            if (!desc1.AttachedToDesktop)
                                continue;

                            RawRect rect = desc1.DesktopCoordinates;
                            int sx = rect.Left, sy = rect.Top;
                            int sw = rect.Right - rect.Left;
                            int sh = rect.Bottom - rect.Top;
                            if (sw <= 0 || sh <= 0)
                                continue;

                            int ix0 = Math.Max(x, sx);
                            int iy0 = Math.Max(y, sy);
                            int ix1 = Math.Min(x + width, sx + sw);
                            int iy1 = Math.Min(y + height, sy + sh);
                            if (ix1 <= ix0 || iy1 <= iy0)
                                continue;

                            var featureLevels = new[]
                            {
                                FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
                            };
                            Result devRes = D3D11.D3D11CreateDevice(
                                adapter,
                                DriverType.Unknown,
                                DeviceCreationFlags.BgraSupport,
                                featureLevels,
                                out device,
                                out context);
                            if (devRes.Failure || device is null || context is null)
                            {
                                diagnostics.Add($"adapter#{adapterIndex} output#{outputIndex} D3D11CreateDevice 失败（0x{devRes.Code:X8}），跳过该输出。");
                                continue;
                            }

                            var outputCapture = new RegionOutputCapture(
                                device,
                                context,
                                output,
                                output6,
                                outputIndex,
                                isHdr: desc1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020,
                                sourceX: ix0 - sx,
                                sourceY: iy0 - sy,
                                copyWidth: ix1 - ix0,
                                copyHeight: iy1 - iy0,
                                destX: ix0 - x,
                                destY: iy0 - y);
                            outputCapture.RecreateDuplication(diagnostics);
                            outputs.Add(outputCapture);
                            ownershipTransferred = true;
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add($"adapter#{adapterIndex} output#{outputIndex} 区域连续抓帧会话初始化失败：{ex.GetType().Name} {ex.Message}。");
                        }
                        finally
                        {
                            if (!ownershipTransferred)
                            {
                                context?.Dispose();
                                device?.Dispose();
                                output6?.Dispose();
                                output.Dispose();
                            }
                        }
                    }
                }
                finally
                {
                    adapter.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"区域连续抓帧会话顶层异常：{ex.GetType().Name} {ex.Message}（后续仅使用 GDI 基底）。");
        }
        finally
        {
            factory?.Dispose();
        }

        return new RegionCaptureSession(
            x, y, width, height,
            captureSdrWithDesktopDuplication,
            sdrAcquireTotalBudgetMs,
            hdrSdrWhiteNits,
            hdrPeakNits,
            outputs,
            diagnostics);
    }

    private static void SetLastDiagnostics(List<string> diagnostics)
        => _lastDiagnostics = diagnostics.Count == 0 ? Array.Empty<string>() : diagnostics.ToArray();

    private delegate void OutputCaptureHandler(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIOutput output,
        uint adapterIndex,
        uint outputIndex);

    private static bool ForEachOutput(
        List<string> diag,
        string factoryFailureMessage,
        string topLevelFailurePrefix,
        Func<uint, uint, Exception, string> outputFailureMessage,
        OutputCaptureHandler handleOutput)
    {
        IDXGIFactory1? factory = null;
        try
        {
            if (DXGI.CreateDXGIFactory1(out factory).Failure || factory is null)
            {
                diag.Add(factoryFailureMessage);
                return false;
            }

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Failure || adapter is null)
                    break;

                ID3D11Device? device = null;
                ID3D11DeviceContext? context = null;
                try
                {
                    var featureLevels = new[]
                    {
                        FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
                    };
                    Result devRes = D3D11.D3D11CreateDevice(
                        adapter,
                        DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport,
                        featureLevels,
                        out device,
                        out context);
                    if (devRes.Failure || device is null || context is null)
                    {
                        diag.Add($"adapter#{adapterIndex} D3D11CreateDevice 失败（0x{devRes.Code:X8}），跳过该适配器。");
                        continue;
                    }

                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        if (adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Failure || output is null)
                            break;

                        try
                        {
                            handleOutput(device, context, output, adapterIndex, outputIndex);
                        }
                        catch (Exception ex)
                        {
                            diag.Add(outputFailureMessage(adapterIndex, outputIndex, ex));
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                }
                finally
                {
                    context?.Dispose();
                    device?.Dispose();
                    adapter.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            diag.Add($"{topLevelFailurePrefix}：{ex.GetType().Name} {ex.Message}（返回已合成部分）。");
        }
        finally
        {
            factory?.Dispose();
        }

        return true;
    }

    /// <summary>
    /// 抓 GDI 整屏作为画布基底（top-down BGRA8，stride=W*4）。失败或尺寸不符则回退全黑不透明画布，
    /// 保证后续 DD 覆盖与拼贴的缓冲区尺寸始终正确。
    /// </summary>
    private static byte[] BuildGdiBaseCanvas(int canvasW, int canvasH, List<string> diag)
    {
        long len = (long)canvasW * canvasH * 4;
        try
        {
            var gdi = GdiCapture.CaptureVirtualScreen();
            if (gdi.Width == canvasW && gdi.Height == canvasH && gdi.PixelsBgra.LongLength == len)
                return gdi.PixelsBgra;
            diag.Add($"GDI 基底尺寸不符（{gdi.Width}x{gdi.Height} vs {canvasW}x{canvasH}），回退全黑基底。");
        }
        catch (Exception ex)
        {
            diag.Add($"GDI 基底抓取失败（{ex.GetType().Name} {ex.Message}），回退全黑基底。");
        }
        var black = new byte[len];
        for (long i = 3; i < black.LongLength; i += 4) black[i] = 255;
        return black;
    }

    private static byte[] BuildGdiRegionCanvas(int x, int y, int width, int height, List<string> diag)
    {
        long len = (long)width * height * 4;
        try
        {
            var gdi = GdiCapture.CaptureRegion(x, y, width, height);
            if (gdi.Width == width && gdi.Height == height && gdi.PixelsBgra.LongLength == len)
                return gdi.PixelsBgra;
            diag.Add($"GDI 区域基底尺寸不符（{gdi.Width}x{gdi.Height} vs {width}x{height}），回退全黑基底。");
        }
        catch (Exception ex)
        {
            diag.Add($"GDI 区域基底抓取失败（{ex.GetType().Name} {ex.Message}），回退全黑基底。");
        }

        var black = new byte[len];
        for (long i = 3; i < black.LongLength; i += 4) black[i] = 255;
        return black;
    }

    private static void CaptureOneOutput(
        ID3D11Device device, ID3D11DeviceContext context, IDXGIOutput output,
        VirtualScreenInfo vs, byte[] canvas, int canvasW, int canvasH,
        bool gdiBaseHasVisibleContent,
        bool captureSdrWithDesktopDuplication,
        int sdrAcquireTotalBudgetMs,
        double hdrSdrWhiteNits,
        double hdrPeakNits,
        List<string> diag,
        uint adapterIndex,
        uint outputIndex)
    {
        // 取 Output6 以拿 ColorSpace + DesktopCoordinates；并用作 DuplicateOutput1 的载体。
        IDXGIOutput6? output6;
        try
        {
            output6 = output.QueryInterface<IDXGIOutput6>();
        }
        catch (Exception ex)
        {
            diag.Add($"output#{outputIndex} 不支持 IDXGIOutput6（{ex.GetType().Name}），跳过。");
            return;
        }

        IDXGIOutputDuplication? duplication = null;
        try
        {
            OutputDescription1 desc1 = output6.Description1;
            if (!desc1.AttachedToDesktop)
                return;

            RawRect rect = desc1.DesktopCoordinates;
            int sx = rect.Left, sy = rect.Top;
            int sw = rect.Right - rect.Left;
            int sh = rect.Bottom - rect.Top;
            if (sw <= 0 || sh <= 0)
                return;

            bool isHdr = desc1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;

            if (!isHdr && gdiBaseHasVisibleContent && !captureSdrWithDesktopDuplication)
            {
                // SDR 屏：GDI 基底已是完美 SDR 内容，无需 Desktop Duplication。直接保留基底，
                // 既省去逐屏 DD 的超时/并发开销，也避免 SDR 屏因 DD 失败而变黑。
                return;
            }

            // 支持格式白名单。微软文档要求**必须包含 B8G8R8A8_UNorm**（桌面最常见 scan-out 格式），
            // 否则 SDR 桌面会 DXGI_ERROR_UNSUPPORTED。DXGI 据当前桌面模式择一返回：
            //  - HDR 屏：scan-out 为 FP16 → 返回 R16G16B16A16_Float（scRGB 线性），走 tone map。
            //  - SDR 屏：scan-out 为 8bit → 返回 B8G8R8A8_UNorm（已 sRGB 编码），直接拷贝原样还原。
            // 把 FP16 放第一位表达对 HDR 高精度的偏好。实际返回格式以纹理 Desc.Format 为准（见 ProcessFrame）。
            duplication = TryCreateDuplication(device, output, output6, diag, outputIndex);
            if (duplication is null)
                return;

            var watch = Stopwatch.StartNew();
            bool sawPlaceholderFrame = false;
            bool sawBlackFrame = false;
            int timeouts = 0;
            int accessLostRecreates = 0;
            int acquireBudgetMs = isHdr
                ? AcquireTotalBudgetMs
                : Math.Clamp(sdrAcquireTotalBudgetMs, AcquireTimeoutMs, AcquireTotalBudgetMs);

            // 获取一帧（首帧常为占位/空黑帧，必须验证后再使用）。
            for (int attempt = 1; attempt <= AcquireMaxAttempts && watch.ElapsedMilliseconds <= acquireBudgetMs; attempt++)
            {
                IDXGIResource? frameResource = null;
                Result ar = duplication.AcquireNextFrame(AcquireTimeoutMs, out OutduplFrameInfo frameInfo, out frameResource);
                if (ar == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    frameResource?.Dispose();
                    timeouts++;
                    continue; // 无新帧，重试
                }
                if (ar.Failure)
                {
                    frameResource?.Dispose();
                    if (IsDxgiError(ar, DxgiErrorAccessLost))
                    {
                        if (accessLostRecreates == 0)
                        {
                            accessLostRecreates++;
                            diag.Add($"output#{outputIndex} Desktop Duplication access lost（0x{ar.Code:X8}），重建 duplication 后重试。");
                            duplication.Dispose();
                            duplication = TryCreateDuplication(device, output, output6, diag, outputIndex);
                            if (duplication is null)
                                return;
                            continue;
                        }

                        diag.Add($"output#{outputIndex} Desktop Duplication access lost 重建后仍失败（0x{ar.Code:X8}），保留当前基底。");
                        return;
                    }

                    diag.Add($"output#{outputIndex} AcquireNextFrame 失败（0x{ar.Code:X8}），保留当前基底。");
                    return;
                }
                if (frameResource is null)
                {
                    timeouts++;
                    continue;
                }

                try
                {
                    if (frameInfo.ProtectedContentMaskedOut)
                        diag.Add($"output#{outputIndex} 含受保护内容（已被屏蔽为黑）。");

                    if (frameInfo.LastPresentTime == 0)
                    {
                        if (!sawPlaceholderFrame)
                            diag.Add($"output#{outputIndex} 丢弃 LastPresentTime=0 的占位帧。");
                        sawPlaceholderFrame = true;
                        continue;
                    }

                    if (!TryProcessFrame(device, context, frameResource, isHdr,
                            hdrSdrWhiteNits, hdrPeakNits,
                            out byte[] bgra, out int texW, out int texH, diag, outputIndex))
                    {
                        return;
                    }

                    if (!CapturedImage.HasVisibleContent(texW, texH, bgra))
                    {
                        if (!sawBlackFrame)
                        {
                            diag.Add($"output#{outputIndex} 丢弃全黑帧（可能是 Desktop Duplication 空帧或受保护/远程显示内容）。");
                            sawBlackFrame = true;
                        }
                        continue;
                    }

                    // 拼贴到虚拟桌面画布（按 DesktopCoordinates 相对原点偏移，做边界裁剪）。
                    BlitInto(bgra, texW, texH, sx - vs.X, sy - vs.Y, canvas, canvasW, canvasH);
                    return;
                }
                finally
                {
                    frameResource.Dispose();
                    duplication.ReleaseFrame();
                }
            }

            diag.Add($"output#{outputIndex} 未获得有效非黑帧（timeouts={timeouts}，elapsed={watch.ElapsedMilliseconds}ms），保留当前基底。");
        }
        finally
        {
            duplication?.Dispose();
            output6.Dispose();
        }
    }

    private static void CaptureOneOutputRegion(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIOutput output,
        int regionX,
        int regionY,
        int regionWidth,
        int regionHeight,
        byte[] canvas,
        bool gdiBaseHasVisibleContent,
        bool captureSdrWithDesktopDuplication,
        int sdrAcquireTotalBudgetMs,
        double hdrSdrWhiteNits,
        double hdrPeakNits,
        List<string> diag,
        uint adapterIndex,
        uint outputIndex)
    {
        IDXGIOutput6? output6;
        try
        {
            output6 = output.QueryInterface<IDXGIOutput6>();
        }
        catch (Exception ex)
        {
            diag.Add($"output#{outputIndex} 不支持 IDXGIOutput6（{ex.GetType().Name}），跳过。");
            return;
        }

        IDXGIOutputDuplication? duplication = null;
        try
        {
            OutputDescription1 desc1 = output6.Description1;
            if (!desc1.AttachedToDesktop)
                return;

            RawRect rect = desc1.DesktopCoordinates;
            int sx = rect.Left, sy = rect.Top;
            int sw = rect.Right - rect.Left;
            int sh = rect.Bottom - rect.Top;
            if (sw <= 0 || sh <= 0)
                return;

            int ix0 = Math.Max(regionX, sx);
            int iy0 = Math.Max(regionY, sy);
            int ix1 = Math.Min(regionX + regionWidth, sx + sw);
            int iy1 = Math.Min(regionY + regionHeight, sy + sh);
            if (ix1 <= ix0 || iy1 <= iy0)
                return;

            int sourceX = ix0 - sx;
            int sourceY = iy0 - sy;
            int copyWidth = ix1 - ix0;
            int copyHeight = iy1 - iy0;
            int destX = ix0 - regionX;
            int destY = iy0 - regionY;
            bool isHdr = desc1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;

            if (!isHdr && gdiBaseHasVisibleContent && !captureSdrWithDesktopDuplication)
                return;

            duplication = TryCreateDuplication(device, output, output6, diag, outputIndex);
            if (duplication is null)
                return;

            var watch = Stopwatch.StartNew();
            bool sawPlaceholderFrame = false;
            bool sawBlackFrame = false;
            int timeouts = 0;
            int accessLostRecreates = 0;
            int acquireBudgetMs = isHdr
                ? AcquireTotalBudgetMs
                : Math.Clamp(sdrAcquireTotalBudgetMs, AcquireTimeoutMs, AcquireTotalBudgetMs);

            for (int attempt = 1; attempt <= AcquireMaxAttempts && watch.ElapsedMilliseconds <= acquireBudgetMs; attempt++)
            {
                IDXGIResource? frameResource = null;
                Result ar = duplication.AcquireNextFrame(AcquireTimeoutMs, out OutduplFrameInfo frameInfo, out frameResource);
                if (ar == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    frameResource?.Dispose();
                    timeouts++;
                    continue;
                }
                if (ar.Failure)
                {
                    frameResource?.Dispose();
                    if (IsDxgiError(ar, DxgiErrorAccessLost))
                    {
                        if (accessLostRecreates == 0)
                        {
                            accessLostRecreates++;
                            diag.Add($"output#{outputIndex} Desktop Duplication access lost（0x{ar.Code:X8}），重建 duplication 后重试。");
                            duplication.Dispose();
                            duplication = TryCreateDuplication(device, output, output6, diag, outputIndex);
                            if (duplication is null)
                                return;
                            continue;
                        }

                        diag.Add($"output#{outputIndex} Desktop Duplication access lost 重建后仍失败（0x{ar.Code:X8}），保留当前区域基底。");
                        return;
                    }

                    diag.Add($"output#{outputIndex} AcquireNextFrame 失败（0x{ar.Code:X8}），保留当前区域基底。");
                    return;
                }
                if (frameResource is null)
                {
                    timeouts++;
                    continue;
                }

                try
                {
                    if (frameInfo.ProtectedContentMaskedOut)
                        diag.Add($"output#{outputIndex} 含受保护内容（已被屏蔽为黑）。");

                    if (frameInfo.LastPresentTime == 0)
                    {
                        if (!sawPlaceholderFrame)
                            diag.Add($"output#{outputIndex} 丢弃 LastPresentTime=0 的占位帧。");
                        sawPlaceholderFrame = true;
                        continue;
                    }

                    if (!TryProcessFrameRegion(device, context, frameResource, isHdr,
                            sourceX, sourceY, copyWidth, copyHeight,
                            hdrSdrWhiteNits, hdrPeakNits,
                            out byte[] patch, out int patchW, out int patchH, diag, outputIndex))
                    {
                        return;
                    }

                    if (!CapturedImage.HasVisibleContent(patchW, patchH, patch))
                    {
                        if (!sawBlackFrame)
                        {
                            diag.Add($"output#{outputIndex} 丢弃区域全黑帧（可能是 Desktop Duplication 空帧或受保护/远程显示内容）。");
                            sawBlackFrame = true;
                        }
                        continue;
                    }

                    BlitInto(patch, patchW, patchH, destX, destY, canvas, regionWidth, regionHeight);
                    return;
                }
                finally
                {
                    frameResource.Dispose();
                    duplication.ReleaseFrame();
                }
            }

            diag.Add($"output#{outputIndex} 区域未获得有效非黑帧（timeouts={timeouts}，elapsed={watch.ElapsedMilliseconds}ms），保留当前区域基底。");
        }
        finally
        {
            duplication?.Dispose();
            output6.Dispose();
        }
    }

    public sealed class RegionCaptureSession : IDisposable
    {
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _captureSdrWithDesktopDuplication;
        private readonly int _sdrAcquireTotalBudgetMs;
        private readonly double _hdrSdrWhiteNits;
        private readonly double _hdrPeakNits;
        private readonly List<RegionOutputCapture> _outputs;
        private readonly IReadOnlyList<string> _constructionDiagnostics;
        private bool _disposed;

        internal RegionCaptureSession(
            int x,
            int y,
            int width,
            int height,
            bool captureSdrWithDesktopDuplication,
            int sdrAcquireTotalBudgetMs,
            double hdrSdrWhiteNits,
            double hdrPeakNits,
            List<RegionOutputCapture> outputs,
            List<string> constructionDiagnostics)
        {
            _x = x;
            _y = y;
            _width = width;
            _height = height;
            _captureSdrWithDesktopDuplication = captureSdrWithDesktopDuplication;
            _sdrAcquireTotalBudgetMs = sdrAcquireTotalBudgetMs;
            _hdrSdrWhiteNits = hdrSdrWhiteNits;
            _hdrPeakNits = hdrPeakNits;
            _outputs = outputs;
            _constructionDiagnostics = constructionDiagnostics.Count == 0
                ? Array.Empty<string>()
                : constructionDiagnostics.ToArray();
        }

        public CapturedImage Capture()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var diag = _constructionDiagnostics.Count == 0
                ? new List<string>()
                : new List<string>(_constructionDiagnostics);
            byte[] canvas = BuildGdiRegionCanvas(_x, _y, _width, _height, diag);
            bool gdiBaseHasVisibleContent = CapturedImage.HasVisibleContent(_width, _height, canvas);
            if (!gdiBaseHasVisibleContent)
                diag.Add("GDI 区域基底看起来全黑：将对 SDR 屏也尝试 Desktop Duplication。");

            foreach (var output in _outputs)
            {
                try
                {
                    output.CaptureInto(
                        canvas,
                        _width,
                        _height,
                        gdiBaseHasVisibleContent,
                        _captureSdrWithDesktopDuplication,
                        _sdrAcquireTotalBudgetMs,
                        _hdrSdrWhiteNits,
                        _hdrPeakNits,
                        diag);
                }
                catch (Exception ex)
                {
                    diag.Add($"output#{output.OutputIndex} 区域连续抓帧异常：{ex.GetType().Name} {ex.Message}（保留当前区域基底）。");
                }
            }

            SetLastDiagnostics(diag);
            return new CapturedImage(_width, _height, canvas);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            foreach (var output in _outputs)
                output.Dispose();
            _outputs.Clear();
            _disposed = true;
        }
    }

    internal sealed class RegionOutputCapture : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutput _output;
        private readonly IDXGIOutput6 _output6;
        private readonly bool _isHdr;
        private readonly int _sourceX;
        private readonly int _sourceY;
        private readonly int _copyWidth;
        private readonly int _copyHeight;
        private readonly int _destX;
        private readonly int _destY;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Texture2D? _regionStaging;
        private uint _regionStagingWidth;
        private uint _regionStagingHeight;
        private Format _regionStagingFormat;
        private bool _disposed;

        public uint OutputIndex { get; }

        public RegionOutputCapture(
            ID3D11Device device,
            ID3D11DeviceContext context,
            IDXGIOutput output,
            IDXGIOutput6 output6,
            uint outputIndex,
            bool isHdr,
            int sourceX,
            int sourceY,
            int copyWidth,
            int copyHeight,
            int destX,
            int destY)
        {
            _device = device;
            _context = context;
            _output = output;
            _output6 = output6;
            OutputIndex = outputIndex;
            _isHdr = isHdr;
            _sourceX = sourceX;
            _sourceY = sourceY;
            _copyWidth = copyWidth;
            _copyHeight = copyHeight;
            _destX = destX;
            _destY = destY;
        }

        public bool RecreateDuplication(List<string> diag)
        {
            _duplication?.Dispose();
            _duplication = TryCreateDuplication(_device, _output, _output6, diag, OutputIndex);
            return _duplication is not null;
        }

        public void CaptureInto(
            byte[] canvas,
            int regionWidth,
            int regionHeight,
            bool gdiBaseHasVisibleContent,
            bool captureSdrWithDesktopDuplication,
            int sdrAcquireTotalBudgetMs,
            double hdrSdrWhiteNits,
            double hdrPeakNits,
            List<string> diag)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_isHdr && gdiBaseHasVisibleContent && !captureSdrWithDesktopDuplication)
                return;

            if (_duplication is null && !RecreateDuplication(diag))
                return;

            var watch = Stopwatch.StartNew();
            bool sawPlaceholderFrame = false;
            bool sawBlackFrame = false;
            int timeouts = 0;
            int accessLostRecreates = 0;
            int acquireBudgetMs = _isHdr
                ? AcquireTotalBudgetMs
                : Math.Clamp(sdrAcquireTotalBudgetMs, AcquireTimeoutMs, AcquireTotalBudgetMs);

            for (int attempt = 1; attempt <= AcquireMaxAttempts && watch.ElapsedMilliseconds <= acquireBudgetMs; attempt++)
            {
                IDXGIResource? frameResource = null;
                Result ar = _duplication!.AcquireNextFrame(AcquireTimeoutMs, out OutduplFrameInfo frameInfo, out frameResource);
                if (ar == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    frameResource?.Dispose();
                    timeouts++;
                    continue;
                }
                if (ar.Failure)
                {
                    frameResource?.Dispose();
                    if (IsDxgiError(ar, DxgiErrorAccessLost))
                    {
                        if (accessLostRecreates == 0)
                        {
                            accessLostRecreates++;
                            diag.Add($"output#{OutputIndex} Desktop Duplication access lost（0x{ar.Code:X8}），重建连续抓帧 duplication 后重试。");
                            if (!RecreateDuplication(diag))
                                return;
                            continue;
                        }

                        diag.Add($"output#{OutputIndex} Desktop Duplication access lost 重建后仍失败（0x{ar.Code:X8}），保留当前区域基底。");
                        return;
                    }

                    diag.Add($"output#{OutputIndex} AcquireNextFrame 失败（0x{ar.Code:X8}），保留当前区域基底。");
                    return;
                }
                if (frameResource is null)
                {
                    timeouts++;
                    continue;
                }

                try
                {
                    if (frameInfo.ProtectedContentMaskedOut)
                        diag.Add($"output#{OutputIndex} 含受保护内容（已被屏蔽为黑）。");

                    if (frameInfo.LastPresentTime == 0)
                    {
                        if (!sawPlaceholderFrame)
                            diag.Add($"output#{OutputIndex} 丢弃 LastPresentTime=0 的占位帧。");
                        sawPlaceholderFrame = true;
                        continue;
                    }

                    if (!TryProcessFrameRegion(
                            _device,
                            _context,
                            frameResource,
                            _isHdr,
                            _sourceX,
                            _sourceY,
                            _copyWidth,
                            _copyHeight,
                            hdrSdrWhiteNits,
                            hdrPeakNits,
                            out byte[] patch,
                            out int patchW,
                            out int patchH,
                            diag,
                            OutputIndex,
                            GetOrCreateRegionStaging))
                    {
                        return;
                    }

                    if (!CapturedImage.HasVisibleContent(patchW, patchH, patch))
                    {
                        if (!sawBlackFrame)
                        {
                            diag.Add($"output#{OutputIndex} 丢弃区域全黑帧（可能是 Desktop Duplication 空帧或受保护/远程显示内容）。");
                            sawBlackFrame = true;
                        }
                        continue;
                    }

                    BlitInto(patch, patchW, patchH, _destX, _destY, canvas, regionWidth, regionHeight);
                    return;
                }
                finally
                {
                    frameResource.Dispose();
                    _duplication.ReleaseFrame();
                }
            }

            diag.Add($"output#{OutputIndex} 区域未获得有效非黑帧（timeouts={timeouts}，elapsed={watch.ElapsedMilliseconds}ms），保留当前区域基底。");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _regionStaging?.Dispose();
            _regionStaging = null;
            _duplication?.Dispose();
            _output6.Dispose();
            _output.Dispose();
            _context.Dispose();
            _device.Dispose();
            _disposed = true;
        }

        private ID3D11Texture2D GetOrCreateRegionStaging(Texture2DDescription sourceDescription, int width, int height)
        {
            uint targetWidth = (uint)width;
            uint targetHeight = (uint)height;
            if (_regionStaging is not null &&
                _regionStagingWidth == targetWidth &&
                _regionStagingHeight == targetHeight &&
                _regionStagingFormat == sourceDescription.Format)
            {
                return _regionStaging;
            }

            _regionStaging?.Dispose();
            var stagingDesc = new Texture2DDescription
            {
                Width = targetWidth,
                Height = targetHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = sourceDescription.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };

            _regionStaging = _device.CreateTexture2D(in stagingDesc);
            _regionStagingWidth = targetWidth;
            _regionStagingHeight = targetHeight;
            _regionStagingFormat = sourceDescription.Format;
            return _regionStaging;
        }
    }

    private static bool IsDxgiError(Result result, uint code)
        => unchecked((uint)result.Code) == code;

    private static IDXGIOutputDuplication? TryCreateDuplication(
        ID3D11Device device,
        IDXGIOutput output,
        IDXGIOutput6 output6,
        List<string> diag,
        uint outputIndex)
    {
        try
        {
            return output6.DuplicateOutput1(
                device,
                new[] { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm });
        }
        catch (Exception ex)
        {
            // DXGI_ERROR_UNSUPPORTED：混合显卡的独显、8bpp/非 DWM 桌面、会话断开等；
            // DXGI_ERROR_NOT_CURRENTLY_AVAILABLE：已达并发 duplication 上限（默认 4）；
            // E_ACCESSDENIED：当前桌面图像无访问权限（安全桌面、部分远程/虚拟显示链路等）。
            diag.Add($"output#{outputIndex} DuplicateOutput1 失败（{ex.GetType().Name} {ex.Message}），尝试 DuplicateOutput fallback。");
        }

        IDXGIOutput1? output1 = null;
        try
        {
            output1 = output.QueryInterface<IDXGIOutput1>();
            return output1.DuplicateOutput(device);
        }
        catch (Exception ex)
        {
            diag.Add($"output#{outputIndex} DuplicateOutput fallback 失败（{ex.GetType().Name} {ex.Message}），保留当前基底。");
            return null;
        }
        finally
        {
            output1?.Dispose();
        }
    }

    private static bool TryProcessFrame(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIResource frameResource,
        bool isHdr,
        double hdrSdrWhiteNits,
        double hdrPeakNits,
        out byte[] bgra, out int texW, out int texH,
        List<string> diag, uint outputIndex)
    {
        bgra = Array.Empty<byte>();
        texW = 0;
        texH = 0;

        ID3D11Texture2D? frameTex = frameResource.QueryInterface<ID3D11Texture2D>();
        ID3D11Texture2D? staging = null;
        try
        {
            Texture2DDescription srcDesc = frameTex.Description;

            // CPU 可读的 staging 副本（保留原格式，去掉 bind/misc）。
            var stagingDesc = new Texture2DDescription
            {
                Width = srcDesc.Width,
                Height = srcDesc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = srcDesc.Format, // 实际为 R16G16B16A16_Float 或 B8G8R8A8_UNorm
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };
            staging = device.CreateTexture2D(in stagingDesc);
            context.CopyResource(staging, frameTex);

            texW = (int)srcDesc.Width;
            texH = (int)srcDesc.Height;

            Result mapRes = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource map);
            if (mapRes.Failure || map.DataPointer == IntPtr.Zero)
            {
                diag.Add($"output#{outputIndex} Map 失败（0x{mapRes.Code:X8}），保留当前基底。");
                return false;
            }

            try
            {
                if (srcDesc.Format == Format.R16G16B16A16_Float)
                {
                    // HDR 路径：FP16 scRGB（线性，(1,1,1)=80nits）→ ToneMapper → SDR BGRA8。
                    // 原色为 Rec.709，传 inputIsRec2020:false 跳过 Rec.2020→709 矩阵。
                    bgra = MapScRgbFp16ToSdrBgra(
                        map.DataPointer, (int)map.RowPitch, texW, texH,
                        hdrSdrWhiteNits, hdrPeakNits,
                        inputIsRec2020: false);
                }
                else if (srcDesc.Format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb)
                {
                    // SDR 路径：B8G8R8A8_UNorm 已是 sRGB 编码的 BGRA8，直接按行紧凑拷贝（无需 tone map），
                    // 即任务所述「SDR 屏直接请求 BGRA8」方案，完全原样还原 SDR 画面。Alpha 置不透明。
                    bgra = ReadBgra8(map.DataPointer, (int)map.RowPitch, texW, texH);
                }
                else if (srcDesc.Format is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb)
                {
                    bgra = ReadRgba8AsBgra(map.DataPointer, (int)map.RowPitch, texW, texH);
                }
                else
                {
                    diag.Add($"output#{outputIndex} 不支持的 Desktop Duplication 帧格式：{srcDesc.Format}，保留当前基底。");
                    return false;
                }
            }
            finally
            {
                context.Unmap(staging, 0);
            }
            return true;
        }
        finally
        {
            staging?.Dispose();
            frameTex.Dispose();
        }
    }

    private static bool TryProcessFrameRegion(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIResource frameResource,
        bool isHdr,
        int sourceX,
        int sourceY,
        int width,
        int height,
        double hdrSdrWhiteNits,
        double hdrPeakNits,
        out byte[] bgra,
        out int texW,
        out int texH,
        List<string> diag,
        uint outputIndex,
        Func<Texture2DDescription, int, int, ID3D11Texture2D>? stagingFactory = null)
    {
        bgra = Array.Empty<byte>();
        texW = 0;
        texH = 0;

        ID3D11Texture2D? frameTex = frameResource.QueryInterface<ID3D11Texture2D>();
        ID3D11Texture2D? staging = null;
        try
        {
            Texture2DDescription srcDesc = frameTex.Description;
            int srcW = (int)srcDesc.Width;
            int srcH = (int)srcDesc.Height;
            if (sourceX < 0 || sourceY < 0 || width <= 0 || height <= 0 || sourceX >= srcW || sourceY >= srcH)
            {
                diag.Add($"output#{outputIndex} 区域源矩形无效：({sourceX},{sourceY},{width},{height}) / {srcW}x{srcH}。");
                return false;
            }

            texW = Math.Min(width, srcW - sourceX);
            texH = Math.Min(height, srcH - sourceY);
            if (texW <= 0 || texH <= 0)
                return false;

            if (stagingFactory is null)
            {
                var stagingDesc = new Texture2DDescription
                {
                    Width = (uint)texW,
                    Height = (uint)texH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = srcDesc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                };
                staging = device.CreateTexture2D(in stagingDesc);
            }
            else
            {
                staging = stagingFactory(srcDesc, texW, texH);
            }

            var sourceBox = new Box(sourceX, sourceY, 0, sourceX + texW, sourceY + texH, 1);
            context.CopySubresourceRegion(staging, 0, 0, 0, 0, frameTex, 0, sourceBox);

            Result mapRes = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource map);
            if (mapRes.Failure || map.DataPointer == IntPtr.Zero)
            {
                diag.Add($"output#{outputIndex} 区域 Map 失败（0x{mapRes.Code:X8}），保留当前基底。");
                return false;
            }

            try
            {
                if (srcDesc.Format == Format.R16G16B16A16_Float)
                {
                    bgra = MapScRgbFp16ToSdrBgra(
                        map.DataPointer, (int)map.RowPitch, texW, texH,
                        hdrSdrWhiteNits, hdrPeakNits,
                        inputIsRec2020: false);
                }
                else if (srcDesc.Format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb)
                {
                    bgra = ReadBgra8(map.DataPointer, (int)map.RowPitch, texW, texH);
                }
                else if (srcDesc.Format is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb)
                {
                    bgra = ReadRgba8AsBgra(map.DataPointer, (int)map.RowPitch, texW, texH);
                }
                else
                {
                    diag.Add($"output#{outputIndex} 不支持的 Desktop Duplication 区域帧格式：{srcDesc.Format}，保留当前基底。");
                    return false;
                }
            }
            finally
            {
                context.Unmap(staging, 0);
            }

            return true;
        }
        finally
        {
            if (stagingFactory is null)
                staging?.Dispose();
            frameTex.Dispose();
        }
    }

    private static unsafe byte[] MapScRgbFp16ToSdrBgra(
        IntPtr dataPtr,
        int rowPitch,
        int w,
        int h,
        double sdrWhiteNits,
        double hdrPeakNits,
        bool inputIsRec2020)
    {
        if (sdrWhiteNits <= 0) throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits));
        if (hdrPeakNits <= 0) throw new ArgumentOutOfRangeException(nameof(hdrPeakNits));
        if (w < 0) throw new ArgumentOutOfRangeException(nameof(w));
        if (h < 0) throw new ArgumentOutOfRangeException(nameof(h));

        long byteCount = (long)w * h * 4;
        if (byteCount > int.MaxValue)
            throw new InvalidOperationException($"HDR 帧过大，无法展开为 SDR 缓冲：{w}x{h}。");

        var dst = new byte[(int)byteCount];
        var mapping = ToneMapper.CreateMappingParameters(
            sdrWhiteNits,
            hdrPeakNits,
            inputIsRec2020);
        byte* basePtr = (byte*)dataPtr;
        for (int y = 0; y < h; y++)
        {
            ushort* row = (ushort*)(basePtr + (long)y * rowPitch);
            int dstRow = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int sp = x * 4;
                int dp = dstRow + sp;
                var (b, g, r, a) = ToneMapper.MapScRgbPixelToSdrBgra(
                    (float)BitConverter.UInt16BitsToHalf(row[sp + 0]),
                    (float)BitConverter.UInt16BitsToHalf(row[sp + 1]),
                    (float)BitConverter.UInt16BitsToHalf(row[sp + 2]),
                    (float)BitConverter.UInt16BitsToHalf(row[sp + 3]),
                    mapping);

                dst[dp] = b;
                dst[dp + 1] = g;
                dst[dp + 2] = r;
                dst[dp + 3] = a;
            }
        }

        return dst;
    }

    /// <summary>
    /// 从映射的 <c>B8G8R8A8_UNorm</c> 缓冲读出紧凑 BGRA8（stride=w*4，top-down），按 <paramref name="rowPitch"/>
    /// 跳过行尾对齐填充。Alpha 强制 255（桌面 duplication 的 A 通道未定义）。
    /// </summary>
    private static unsafe byte[] ReadBgra8(IntPtr dataPtr, int rowPitch, int w, int h)
    {
        int byteCount = checked(w * h * 4);
        var dst = new byte[byteCount];
        int sourceBytes = h == 0 ? 0 : checked(((h - 1) * rowPitch) + (w * 4));
        var source = new ReadOnlySpan<byte>((void*)dataPtr, sourceBytes);
        BgraBufferConverter.CopyBgra8ToOpaqueBgra(source, rowPitch, dst, w, h);
        return dst;
    }

    private static unsafe byte[] ReadRgba8AsBgra(IntPtr dataPtr, int rowPitch, int w, int h)
    {
        int byteCount = checked(w * h * 4);
        var dst = new byte[byteCount];
        int sourceBytes = h == 0 ? 0 : checked(((h - 1) * rowPitch) + (w * 4));
        var source = new ReadOnlySpan<byte>((void*)dataPtr, sourceBytes);
        BgraBufferConverter.CopyRgba8ToOpaqueBgra(source, rowPitch, dst, w, h);
        return dst;
    }

    /// <summary>
    /// 把一块屏的 BGRA8（紧凑 stride=w*4，top-down）拷入虚拟桌面画布的 (destX,destY) 处，做边界裁剪。
    /// </summary>
    private static void BlitInto(
        byte[] src, int srcW, int srcH, int destX, int destY,
        byte[] canvas, int canvasW, int canvasH)
    {
        int x0 = Math.Max(0, destX);
        int y0 = Math.Max(0, destY);
        int x1 = Math.Min(canvasW, destX + srcW);
        int y1 = Math.Min(canvasH, destY + srcH);
        if (x1 <= x0 || y1 <= y0) return;

        int copyBytes = (x1 - x0) * 4;
        for (int y = y0; y < y1; y++)
        {
            int srcRow = ((y - destY) * srcW + (x0 - destX)) * 4;
            int dstRow = (y * canvasW + x0) * 4;
            Array.Copy(src, srcRow, canvas, dstRow, copyBytes);
        }
    }
}
