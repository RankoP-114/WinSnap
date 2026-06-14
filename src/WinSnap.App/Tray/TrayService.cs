using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Serilog;

namespace WinSnap.App.Tray;

/// <summary>
/// 系统托盘常驻图标与右键菜单。左键单击 = 截图，菜单含截图/设置/退出。
/// </summary>
public sealed class TrayService : IDisposable
{
    private TaskbarIcon? _icon;

    /// <summary>请求开始截图（托盘左键或菜单触发）。</summary>
    public event Action? CaptureRequested;

    /// <summary>请求打开设置窗口。</summary>
    public event Action? SettingsRequested;

    /// <summary>请求长截图（滚动拼接）。</summary>
    public event Action? LongCaptureRequested;

    public void Initialize()
    {
        _icon = new TaskbarIcon
        {
            Icon = LoadAppIcon(),
            ToolTipText = "WinSnap 截图工具",
        };

        var menu = new ContextMenu();

        var captureItem = new MenuItem { Header = "截图 (Ctrl+Alt+A)" };
        captureItem.Click += (_, _) => CaptureRequested?.Invoke();
        menu.Items.Add(captureItem);

        var longCaptureItem = new MenuItem { Header = "长截图（滚动）" };
        longCaptureItem.Click += (_, _) => LongCaptureRequested?.Invoke();
        menu.Items.Add(longCaptureItem);

        var settingsItem = new MenuItem { Header = "设置..." };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) =>
        {
            Log.Information("用户从托盘退出");
            Application.Current.Shutdown();
        };
        menu.Items.Add(exitItem);

        _icon.ContextMenu = menu;
        _icon.TrayLeftMouseUp += (_, _) => CaptureRequested?.Invoke();

        ShowMessage("WinSnap", "已在后台运行，按 Ctrl+Alt+A 截图");
        Log.Information("托盘已初始化");
    }

    /// <summary>使用系统默认应用图标，避免仓库依赖二进制 ico 资源。</summary>
    private static System.Drawing.Icon LoadAppIcon() => System.Drawing.SystemIcons.Application;

    /// <summary>弹出气泡提示。</summary>
    public void ShowMessage(string title, string message)
    {
        _icon?.ShowBalloonTip(title, message, BalloonIcon.Info);
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
