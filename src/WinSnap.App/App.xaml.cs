using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using WinSnap.App.Capture;
using WinSnap.App.Hotkeys;
using WinSnap.App.Tray;
using WinSnap.Core.Settings;

namespace WinSnap.App;

/// <summary>
/// 应用入口：单实例保护 + DI 容器 + Serilog 日志 + 托盘常驻 + 全局热键。
/// ShutdownMode=OnExplicitShutdown（见 App.xaml），无主窗口，靠托盘/热键驱动。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "WinSnap.SingleInstance.{B7E3F0A1-9C2D-4E55-8A6B-WINSNAP}";
    private const string WakeEventName = "WinSnap.WakeFirstInstance.{B7E3F0A1-9C2D-4E55-8A6B-WINSNAP}";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _wakeEvent;
    private RegisteredWaitHandle? _wakeRegistration;
    private ServiceProvider? _services;
    private TrayService? _tray;
    private HotkeyManager? _hotkeys;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 单实例保护 ----
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            if (!SignalFirstInstance())
            {
                MessageBox.Show("WinSnap 已在运行中。", "WinSnap",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);

        // ---- 配置 ----
        var settingsService = new SettingsService();
        settingsService.Load();

        // ---- 日志 ----
        ConfigureLogging(settingsService.Current.EnableLogging);

        Log.Information("WinSnap 启动，配置文件：{Path}", settingsService.FilePath);
        if (settingsService.LastLoadError is not null)
            Log.Warning(settingsService.LastLoadError, "配置文件读取失败，已回退默认设置");

        // ---- 全局异常处理 ----
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.ExceptionObject as Exception, "未处理的 AppDomain 异常");

        // ---- DI 容器 ----
        var sc = new ServiceCollection();
        sc.AddSingleton(settingsService);
        sc.AddSingleton<TrayService>();
        sc.AddSingleton<CaptureController>();
        sc.AddSingleton<HotkeyManager>();
        _services = sc.BuildServiceProvider();

        var capture = _services.GetRequiredService<CaptureController>();

        // ---- 主题（WinUI3 Fluent，跟随设置）----
        WinSnap.App.Theming.ThemeManager.Apply(settingsService.Current.Theme);

        // ---- 托盘 ----
        _tray = _services.GetRequiredService<TrayService>();
        _tray.CaptureRequested += capture.StartCapture;
        _tray.LongCaptureRequested += capture.StartLongCapture;
        _tray.GifCaptureRequested += capture.StartGifCaptureWithDurationPrompt;
        _tray.CloseAllPinsRequested += capture.CloseAllPins;
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.Initialize();
        StartWakeListener();

        // ---- 全局热键 ----
        _hotkeys = _services.GetRequiredService<HotkeyManager>();
        _hotkeys.CaptureTriggered += capture.StartCapture;
        _hotkeys.PinTriggered += capture.StartPinCapture;
        _hotkeys.ScrollCaptureTriggered += capture.StartLongCapture;
        bool hotkeyRegistered = _hotkeys.Initialize(settingsService.Current);
        if (!hotkeyRegistered)
        {
            _tray.ShowMessage("全局热键注册失败",
                "截图 / 钉图 / 长截图热键中至少有一个注册失败，可能被其它程序占用。" +
                "可从托盘菜单使用功能，或稍后在设置中更换热键。");
        }

        // 冒烟自检（环境变量门控，不影响正常运行）
        if (Environment.GetEnvironmentVariable("WINSNAP_SELFTEST") == "1")
            RunSelfTest(capture);
    }

    /// <summary>
    /// 冒烟自检：抓取虚拟桌面存 PNG、弹出覆盖层、1.5 秒后自动关闭并退出。
    /// 仅当环境变量 WINSNAP_SELFTEST=1 时启用，用于无需人工交互地验证截图流程。
    /// </summary>
    private void RunSelfTest(CaptureController capture)
    {
        var startTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        startTimer.Tick += (_, _) =>
        {
            startTimer.Stop();
            try
            {
                var img = WinSnap.Interop.GdiCapture.CaptureVirtualScreen();
                var bmp = WinSnap.App.Imaging.ImagingHelper.ToBitmapSource(img);
                var path = Path.Combine(Path.GetTempPath(), "Screenshot_selftest.png");
                using (var fs = File.Create(path))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    enc.Save(fs);
                }
                Log.Information("自检：抓屏已保存 {Path}（{W}x{H}）", path, img.Width, img.Height);

                capture.StartCapture();
                Log.Information("自检：覆盖层已弹出");

                var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                closeTimer.Tick += (_, _) =>
                {
                    closeTimer.Stop();
                    foreach (var w in Windows.OfType<CaptureScreenWindow>().ToArray())
                        w.Close();
                    Log.Information("自检：完成，正常退出");
                    Shutdown();
                };
                closeTimer.Start();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "自检过程异常");
                Shutdown();
            }
        };
        startTimer.Start();
    }

    private void OnSettingsRequested()
    {
        var settings = _services!.GetRequiredService<SettingsService>();
        var window = new WinSnap.App.Settings.SettingsWindow(settings);
        window.HotkeysChanged += (captureHotkey, pinHotkey, scrollHotkey) =>
        {
            bool registered = _hotkeys?.RegisterConfiguredHotkeys(captureHotkey, pinHotkey, scrollHotkey) == true;
            if (!registered)
            {
                _tray?.ShowMessage("全局热键注册失败",
                    "截图 / 钉图 / 长截图热键中至少有一个注册失败，原热键已保留。");
            }
            return registered;
        };
        window.Saved += (_, _) => ConfigureLogging(settings.Current.EnableLogging);
        window.ShowDialog();
    }

    private void StartWakeListener()
    {
        if (_wakeEvent is null)
            return;

        _wakeRegistration = ThreadPool.RegisterWaitForSingleObject(
            _wakeEvent,
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _tray?.ShowMessage("WinSnap", "已在后台运行，可从托盘菜单使用。");
            }),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static bool SignalFirstInstance()
    {
        try
        {
            using var wakeEvent = EventWaitHandle.OpenExisting(WakeEventName);
            return wakeEvent.Set();
        }
        catch
        {
            return false;
        }
    }

    private static void ConfigureLogging(bool enableLogging)
    {
        Log.CloseAndFlush();
        Log.Logger = CreateLogger(enableLogging);
    }

    private static ILogger CreateLogger(bool enableLogging)
    {
        if (!enableLogging)
            return new LoggerConfiguration().CreateLogger();

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSnap");
        var logDir = Path.Combine(appDataDir, "logs");
        Directory.CreateDirectory(logDir);

        return new LoggerConfiguration()
            .MinimumLevel.Is(LogEventLevel.Debug)
            .WriteTo.File(
                Path.Combine(logDir, "winsnap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未处理的 UI 线程异常");
        e.Handled = true; // 记录后不崩溃
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("WinSnap 退出");
        _wakeRegistration?.Unregister(null);
        _wakeRegistration = null;
        _wakeEvent?.Dispose();
        _wakeEvent = null;
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _services?.Dispose();
        Log.CloseAndFlush();

        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* 未拥有，忽略 */ }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
