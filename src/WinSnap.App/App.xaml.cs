using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
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

    private Mutex? _singleInstanceMutex;
    private ServiceProvider? _services;
    private TrayService? _tray;
    private HotkeyManager? _hotkeys;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 单实例保护 ----
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("WinSnap 已在运行中。", "WinSnap",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // ---- 配置 ----
        var settingsService = new SettingsService();
        settingsService.Load();

        // ---- 日志 ----
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSnap");
        var logDir = Path.Combine(appDataDir, "logs");
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "winsnap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("WinSnap 启动，配置文件：{Path}", settingsService.FilePath);

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
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.Initialize();

        // ---- 全局热键 ----
        _hotkeys = _services.GetRequiredService<HotkeyManager>();
        _hotkeys.CaptureTriggered += capture.StartCapture;
        bool hotkeyRegistered = _hotkeys.Initialize(settingsService.Current.CaptureHotkey);
        if (!hotkeyRegistered)
        {
            _tray.ShowMessage("截图热键被占用",
                $"热键 {settingsService.Current.CaptureHotkey} 注册失败，可能被其它程序（如 QQ）占用。" +
                "可从托盘菜单截图，或稍后在设置中更换热键。");
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
                var path = Path.Combine(Path.GetTempPath(), "winsnap_selftest.png");
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
        window.CaptureHotkeyChanged += hk => _hotkeys?.RegisterCaptureHotkey(hk);
        window.ShowDialog();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未处理的 UI 线程异常");
        e.Handled = true; // 记录后不崩溃
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("WinSnap 退出");
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
