namespace WinSnap.Interop;

/// <summary>
/// 虚拟桌面（所有显示器并集）的物理像素矩形。
/// 原点可能为负（副屏在主屏左/上方时）。
/// </summary>
public readonly record struct VirtualScreenInfo(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>从系统指标读取当前虚拟桌面范围。</summary>
    public static VirtualScreenInfo Get() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
}
