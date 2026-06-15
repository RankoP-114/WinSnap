namespace WinSnap.Interop;

/// <summary>
/// 覆盖层窗口的物理像素定位与光标坐标工具。
/// 绕开 WPF 在 PerMonitorV2 + 混合 DPI 下 Window.Left/Top 的歧义（dotnet/wpf#4127）。
/// </summary>
public static class ScreenWindowHelper
{
    /// <summary>把窗口精确定位/铺满指定物理像素矩形（用于按显示器覆盖），置顶且不抢焦点。</summary>
    public static void PositionTopmost(IntPtr hwnd, int x, int y, int width, int height, bool showWindow = true)
    {
        uint flags = NativeMethods.SWP_NOACTIVATE;
        if (showWindow)
            flags |= NativeMethods.SWP_SHOWWINDOW;

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            x, y, width, height, flags);
    }

    /// <summary>释放前台游戏/窗口可能留下的鼠标捕获与裁剪区域。</summary>
    public static void ReleaseCursorConstraints()
    {
        NativeMethods.ReleaseCapture();
        NativeMethods.ClipCursor(IntPtr.Zero);
    }

    /// <summary>让覆盖层窗口接管前台输入，避免全屏游戏继续锁定鼠标。</summary>
    public static void ActivateForInput(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        ReleaseCursorConstraints();
        NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>等待 DWM 消费当前绘制队列；用于覆盖层从屏外移入前避免露出未绘制首帧。</summary>
    public static void FlushDwm()
    {
        try
        {
            NativeMethods.DwmFlush();
        }
        catch
        {
            // DWM 不可用时忽略；截图窗口仍按正常路径显示。
        }
    }

    /// <summary>当前光标的物理像素坐标（屏幕坐标系）。</summary>
    public static (int X, int Y) GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out var p);
        return (p.X, p.Y);
    }
}
