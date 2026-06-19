using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using WinSnap.App.Diagnostics;
using WinSnap.App.Imaging;
using WinSnap.App.Pin;
using WinSnap.App.ScrollCapture;
using WinSnap.Core.Settings;
using WinSnap.Interop;

namespace WinSnap.App.Capture;

/// <summary>
/// 编排截图：捕获虚拟桌面 → 按显示器分屏的 <see cref="CaptureSession"/>（混合 DPI 精确）
/// → 选区/标注/取色 → 复制/保存/钉图。并编排长截图（滚动拼接）。
/// </summary>
public sealed class CaptureController
{
    private readonly record struct OverlayCaptureBackground(BitmapSource Source, bool UseTransparentLiveOverlay);

    private readonly SettingsService _settings;
    private readonly PinManager _pinManager = new();
    private CaptureSession? _session;
    private bool _isGifRecording;

    public CaptureController(SettingsService settings)
    {
        _settings = settings;
    }

    public void StartCapture()
    {
        if (_session is not null || _isGifRecording)
        {
            Log.Debug("已有截图会话或 GIF 录制进行中，忽略本次截图触发");
            return;
        }

        try
        {
            Log.Information("开始截图：捕获虚拟桌面（GDI 路径）");
            var background = CaptureScreenBitmapSource();
            _session = new CaptureSession(
                background.Source,
                defaultSaveFormat: CurrentSaveFormat,
                jpegQuality: CurrentJpegQuality,
                useTransparentLiveOverlay: background.UseTransparentLiveOverlay);
            _session.PinRequested += OnPinRequested;
            _session.Closed += OnSessionClosed;
            _session.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "截图失败");
            _session = null;
        }
    }

    public void StartLongCapture()
    {
        if (_session is not null || _isGifRecording)
        {
            Log.Debug("已有会话或 GIF 录制进行中，忽略长截图触发");
            return;
        }

        try
        {
            Log.Information("开始长截图：先框选要滚动捕获的区域");
            var background = CaptureScreenBitmapSource();
            _session = new CaptureSession(
                background.Source,
                CaptureSession.SessionMode.LongCapture,
                CurrentSaveFormat,
                CurrentJpegQuality,
                background.UseTransparentLiveOverlay);
            _session.LongCaptureConfirmed += OnLongCaptureConfirmed;
            _session.Closed += OnSessionClosed;
            _session.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "长截图启动失败");
            _session = null;
        }
    }

    public void StartPinCapture()
    {
        if (_session is not null || _isGifRecording)
        {
            Log.Debug("已有会话或 GIF 录制进行中，忽略钉图触发");
            return;
        }

        try
        {
            Log.Information("开始钉图：先框选要钉到屏幕的区域");
            var background = CaptureScreenBitmapSource();
            _session = new CaptureSession(
                background.Source,
                CaptureSession.SessionMode.PinCapture,
                CurrentSaveFormat,
                CurrentJpegQuality,
                background.UseTransparentLiveOverlay);
            _session.PinRequested += OnPinRequested;
            _session.Closed += OnSessionClosed;
            _session.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "钉图启动失败");
            _session = null;
        }
    }

    public void StartGifCaptureWithDurationPrompt()
    {
        if (_session is not null || _isGifRecording)
        {
            Log.Debug("已有会话或 GIF 录制进行中，忽略 GIF 触发");
            return;
        }

        var dialog = new GifDurationDialog(
            Math.Clamp(_settings.Current.GifDefaultDurationSeconds, 1, 60));
        if (dialog.ShowDialog() == true)
            StartGifCaptureCore(dialog.DurationSeconds);
    }

    private void StartGifCaptureCore(int? durationOverrideSeconds)
    {
        if (_session is not null || _isGifRecording)
        {
            Log.Debug("已有会话或 GIF 录制进行中，忽略 GIF 触发");
            return;
        }

        try
        {
            var options = GifCaptureOptions.FromSettings(_settings.Current, durationOverrideSeconds);
            Log.Information("开始 GIF 录制：先框选录制区域，时长={Duration}s FPS={Fps} 倒计时={Countdown}s",
                options.DurationSeconds, options.FramesPerSecond, options.CountdownSeconds);
            var background = CaptureScreenBitmapSource();
            _session = new CaptureSession(
                background.Source,
                CaptureSession.SessionMode.GifCapture,
                CurrentSaveFormat,
                CurrentJpegQuality,
                background.UseTransparentLiveOverlay);
            _session.GifCaptureConfirmed += (x, y, w, h, mode) =>
                OnGifCaptureConfirmed(x, y, w, h, mode, options);
            _session.Closed += OnSessionClosed;
            _session.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GIF 录制启动失败");
            _session = null;
            MessageBox.Show($"GIF 录制启动失败：{ex.Message}", "WinSnap",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPinRequested(BitmapSource image, CapturedImage? captured, int x, int y)
        => _pinManager.Pin(image, captured, x, y);

    private async void OnLongCaptureConfirmed(int x, int y, int w, int h)
    {
        // 覆盖层此时已关闭，目标窗口恢复前台，开始自动滚动捕获
        try
        {
            var service = new ScrollCaptureService();
            var result = await service.CaptureAsync(x, y, w, h, CancellationToken.None);
            if (result is not null)
            {
                // 把长图钉到屏幕，用户可右键复制/另存
                _pinManager.Pin(result, null, x, y);
                Log.Information("长截图完成：{W}x{H}", result.PixelWidth, result.PixelHeight);
            }
            else
            {
                Log.Warning("长截图未产生结果（可能目标不响应合成滚轮）");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "长截图过程失败");
        }
    }

    private async void OnGifCaptureConfirmed(
        int x,
        int y,
        int w,
        int h,
        CaptureSession.GifOutputMode outputMode,
        GifCaptureOptions options)
    {
        using var recordingCts = new CancellationTokenSource();
        string? outputPath = null;
        GifRecordingStatusWindow? statusWindow = null;
        GifCaptureFrameWindow? frameWindow = null;
        try
        {
            _isGifRecording = true;
            outputPath = outputMode == CaptureSession.GifOutputMode.Save
                ? PromptGifSavePath()
                : BuildTempGifPath();
            if (string.IsNullOrEmpty(outputPath))
                return;

            await GifCountdownWindow.ShowCountdownAsync(x, y, w, h, options.CountdownSeconds, recordingCts.Token);

            frameWindow = new GifCaptureFrameWindow(x, y, w, h);
            frameWindow.Show();
            await Dispatcher.Yield(DispatcherPriority.Render);

            statusWindow = new GifRecordingStatusWindow(x, y, w, h);
            statusWindow.StopRequested += recordingCts.Cancel;
            statusWindow.Show();
            statusWindow.StartCountdown(options.DurationSeconds);
            await statusWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            var service = new GifCaptureService();
            await Task.Run(
                async () => await service.CaptureAsync(
                    x,
                    y,
                    w,
                    h,
                    options,
                    outputPath,
                    recordingCts.Token).ConfigureAwait(false),
                recordingCts.Token);
            statusWindow.SetSaving();

            if (outputMode == CaptureSession.GifOutputMode.Copy)
            {
                ClipboardHelper.CopyFile(outputPath);
                Log.Information("GIF 已复制为文件：{Path}", outputPath);
            }
            else
            {
                Log.Information("GIF 已保存：{Path}", outputPath);
            }
        }
        catch (OperationCanceledException) when (recordingCts.IsCancellationRequested)
        {
            Log.Information("GIF 录制已停止");
            if (!string.IsNullOrEmpty(outputPath))
            {
                try { File.Delete(outputPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GIF 录制失败");
            if (!string.IsNullOrEmpty(outputPath))
            {
                try { File.Delete(outputPath); } catch { }
            }
            MessageBox.Show($"GIF 录制失败：{ex.Message}", "WinSnap",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (statusWindow is not null)
                statusWindow.StopRequested -= recordingCts.Cancel;
            statusWindow?.Close();
            frameWindow?.Close();
            _isGifRecording = false;
            MemoryTrimmer.TrimAfterCapture();
        }
    }

    private string? PromptGifSavePath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "GIF 动图|*.gif",
            FileName = ImageSaver.BuildDefaultFileName("gif"),
            AddExtension = true,
            DefaultExt = ".gif",
        };

        string? dir = _settings.Current.LastSaveDirectory;
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            dialog.InitialDirectory = dir;

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string BuildTempGifPath()
        => TempFileCleaner.BuildTempPath("gif");

    /// <summary>按显示器 HDR 状态和前台全屏状态选择覆盖层背景。</summary>
    private OverlayCaptureBackground CaptureScreenBitmapSource()
    {
        bool hasHdr = HdrDetector.AnyHdrActive();
        bool foregroundFullscreen = IsForegroundFullscreenLike();
        bool useTransparentLiveOverlay = hasHdr || foregroundFullscreen;
        bool needsDuplicationFallback = false;
        double hdrSdrWhiteNits = Math.Clamp(
            _settings.Current.HdrSdrWhiteLevelNits,
            80.0,
            1000.0);
        double hdrPeakNits = Math.Clamp(_settings.Current.HdrPeakNits, hdrSdrWhiteNits, 4000.0);

        if (!hasHdr)
        {
            try
            {
                var gdi = ScreenBitmapSourceCapture.CaptureVirtualScreenGdi();
                if (ScreenBitmapSourceCapture.HasVisibleContent(gdi))
                {
                    Log.Information("使用 GDI 捕获覆盖层背景：前台全屏={ForegroundFullscreen}", foregroundFullscreen);
                    return PrepareOverlayBackground(gdi, useTransparentLiveOverlay);
                }

                Log.Warning("GDI 捕获结果看起来全黑，改用 Desktop Duplication 兜底");
                needsDuplicationFallback = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "GDI 捕获失败，改用 Desktop Duplication 兜底");
                needsDuplicationFallback = true;
            }
        }

        if (hasHdr || foregroundFullscreen || needsDuplicationFallback)
        {
            Log.Information(
                "使用 Desktop Duplication 捕获覆盖层背景：HDR={HasHdr} 前台全屏={ForegroundFullscreen} SDR白={SdrWhite}nit HDR峰值={HdrPeak}nit",
                hasHdr,
                foregroundFullscreen,
                hdrSdrWhiteNits,
                hdrPeakNits);
            CapturedImage? hdr = DuplicationCapture.CaptureVirtualScreenHdrAware(
                captureSdrWithDesktopDuplication: !hasHdr,
                hdrSdrWhiteNits: hdrSdrWhiteNits,
                hdrPeakNits: hdrPeakNits);
            var diagnostics = hdr.Diagnostics;
            foreach (var line in diagnostics)
                Log.Information("HDR 捕获诊断：{Diag}", line);

            if (!hdr.HasVisibleContent() && diagnostics.Count > 0)
            {
                Log.Warning("HDR 捕获结果全黑，尝试最后一次 GDI→WPF 回退");
                try
                {
                    var gdi = ScreenBitmapSourceCapture.CaptureVirtualScreenGdi();
                    if (ScreenBitmapSourceCapture.HasVisibleContent(gdi))
                        return PrepareOverlayBackground(gdi, useTransparentLiveOverlay);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "GDI 回退抓取失败");
                }

                throw new InvalidOperationException(
                    "HDR 捕获返回全黑图像，已阻止打开黑色覆盖层。诊断：" +
                    string.Join(" | ", diagnostics));
            }

            var source = ImagingHelper.ToBitmapSource(hdr);
            hdr = null;
            return PrepareOverlayBackground(source, useTransparentLiveOverlay);
        }

        return PrepareOverlayBackground(ScreenBitmapSourceCapture.CaptureVirtualScreenGdi(), useTransparentLiveOverlay);
    }

    private static OverlayCaptureBackground PrepareOverlayBackground(BitmapSource source, bool useTransparentLiveOverlay)
    {
        MemoryTrimmer.TrimTransientCaptureBuffers();
        return new OverlayCaptureBackground(source, useTransparentLiveOverlay);
    }

    private static bool IsForegroundFullscreenLike()
    {
        if (!WindowEnumerator.TryGetForegroundWindowBounds(out var foreground))
            return false;

        var monitors = MonitorEnumerator.GetMonitors();
        if (monitors.Count == 0)
        {
            var vs = VirtualScreenInfo.Get();
            monitors = new List<MonitorInfo> { new(vs.X, vs.Y, vs.Width, vs.Height, 96) };
        }

        foreach (var monitor in monitors)
        {
            if (Covers(foreground, monitor, tolerancePx: 8))
                return true;
        }

        return false;
    }

    private static bool Covers(WindowEnumerator.WindowBounds window, MonitorInfo monitor, int tolerancePx)
    {
        int windowRight = window.X + window.Width;
        int windowBottom = window.Y + window.Height;

        return window.X <= monitor.X + tolerancePx
               && window.Y <= monitor.Y + tolerancePx
               && windowRight >= monitor.Right - tolerancePx
               && windowBottom >= monitor.Bottom - tolerancePx
               && window.Width >= monitor.Width - tolerancePx
               && window.Height >= monitor.Height - tolerancePx;
    }

    private void OnSessionClosed()
    {
        _session = null;
        MemoryTrimmer.TrimAfterCapture();
    }

    public void CloseAllPins() => _pinManager.CloseAll();

    private string CurrentSaveFormat
        => string.Equals(_settings.Current.DefaultSaveFormat, "jpg", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(_settings.Current.DefaultSaveFormat, "jpeg", StringComparison.OrdinalIgnoreCase)
            ? "jpg"
            : "png";

    private int CurrentJpegQuality => Math.Clamp(_settings.Current.JpegQuality, 1, 100);
}
