using System.IO;
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

    /// <summary>请求 GIF 录制。</summary>
    public event Action? GifCaptureRequested;

    /// <summary>请求关闭所有钉图窗口。</summary>
    public event Action? CloseAllPinsRequested;

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

        var gifCaptureItem = new MenuItem { Header = "GIF 录制" };
        gifCaptureItem.Click += (_, _) => GifCaptureRequested?.Invoke();
        menu.Items.Add(gifCaptureItem);

        var closePinsItem = new MenuItem { Header = "关闭所有钉图" };
        closePinsItem.Click += (_, _) => CloseAllPinsRequested?.Invoke();
        menu.Items.Add(closePinsItem);

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

    /// <summary>
    /// 优先使用本地未入库 ico 编进 exe 后的关联图标；开发环境下也可直接读取 Assets\WinSnap.ico。
    /// ico 文件由 .gitignore 排除，不进入 GitHub。
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is string exe && File.Exists(exe))
            {
                using var associatedIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (associatedIcon is not null)
                    return (System.Drawing.Icon)associatedIcon.Clone();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取 exe 关联图标失败，尝试本地 ico 文件");
        }

        try
        {
            string localIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WinSnap.ico");
            if (!File.Exists(localIconPath))
                localIconPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "WinSnap.ico"));

            if (File.Exists(localIconPath))
            {
                using var icon = new System.Drawing.Icon(localIconPath);
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取本地 ico 文件失败，回退系统默认图标");
        }

        return System.Drawing.SystemIcons.Application;
    }

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
