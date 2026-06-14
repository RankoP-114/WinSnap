using System.Runtime.InteropServices;

namespace WinSnap.Interop;

/// <summary>
/// 顶层窗口边界检测，用于"窗口/控件自动捕获"时高亮目标窗口。
/// 边界优先取 DWM 扩展框架边界（<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>），它排除了
/// 投影阴影、与可见边框对齐，比 <c>GetWindowRect</c> 更贴合用户感知的窗口外沿。
/// 所有坐标均为物理像素（虚拟桌面坐标系）。
/// </summary>
public static class WindowEnumerator
{
    /// <summary>一个顶层窗口的句柄与物理像素边界。</summary>
    public readonly record struct WindowBounds(IntPtr Handle, int X, int Y, int Width, int Height);

    /// <summary>
    /// 命中给定屏幕点（物理像素）的顶层根窗口及其精确边界；无命中返回 null。
    /// 流程：WindowFromPoint → GetAncestor(GA_ROOT) → DWM 扩展边界（回退 GetWindowRect）。
    /// </summary>
    public static WindowBounds? WindowFromScreenPoint(int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        IntPtr hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero)
            return null;

        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero)
            root = hwnd;

        if (!TryGetBounds(root, out RECT rect))
            return null;

        return ToBounds(root, rect);
    }

    /// <summary>尝试取得当前前台根窗口的物理像素边界。</summary>
    public static bool TryGetForegroundWindowBounds(out WindowBounds bounds)
    {
        bounds = default;

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero)
            root = hwnd;

        if (!TryGetBounds(root, out RECT rect))
            return false;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return false;

        bounds = ToBounds(root, rect);
        return true;
    }

    /// <summary>
    /// 枚举所有可见、未最小化、且有面积的顶层窗口，按 Z 序（最前在先）返回。
    /// 边界使用 DWM 扩展框架边界（回退 GetWindowRect）。
    /// </summary>
    public static IReadOnlyList<WindowBounds> EnumerateTopLevelWindows()
    {
        var result = new List<WindowBounds>();

        // EnumWindows 的回调顺序即 Z 序（自顶向下），逐个追加可保持该次序。
        bool Callback(IntPtr hwnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hwnd))
                return true;
            if (IsIconic(hwnd))
                return true;
            if (!TryGetBounds(hwnd, out RECT rect))
                return true;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
                return true;

            result.Add(ToBounds(hwnd, rect));
            return true; // 继续枚举
        }

        // 保持委托存活，防止枚举期间被 GC 回收。
        var proc = new EnumWindowsProc(Callback);
        EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        return result;
    }

    // ---------------------------------------------------------------------
    // 内部实现
    // ---------------------------------------------------------------------

    /// <summary>优先取 DWM 扩展框架边界；失败回退 GetWindowRect。</summary>
    private static bool TryGetBounds(IntPtr hwnd, out RECT rect)
    {
        int hr = DwmGetWindowAttribute(
            hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>());
        if (hr == 0) // S_OK
            return true;

        return GetWindowRect(hwnd, out rect);
    }

    private static WindowBounds ToBounds(IntPtr hwnd, in RECT rect) =>
        new(hwnd, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    // ---------------------------------------------------------------------
    // 本地结构与常量
    // ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint GA_ROOT = 2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ---------------------------------------------------------------------
    // P/Invoke（本类私有，避免与 NativeMethods.cs 冲突）
    // ---------------------------------------------------------------------

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
}
