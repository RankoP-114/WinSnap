using System.Runtime.InteropServices;

namespace WinSnap.Interop;

/// <summary>单个显示器的物理像素矩形与有效 DPI（进程为 PerMonitorV2，故坐标即物理像素）。</summary>
public readonly record struct MonitorInfo(int X, int Y, int Width, int Height, uint Dpi)
{
    public double Scale => Dpi / 96.0;
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>枚举所有显示器及其各自 DPI，用于按屏创建覆盖窗口（混合 DPI 下精确捕获的前提）。</summary>
public static class MonitorEnumerator
{
    private const int MDT_EFFECTIVE_DPI = 0;

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr data)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                uint dpiX = 96, dpiY = 96;
                GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
                var r = mi.rcMonitor;
                list.Add(new MonitorInfo(r.left, r.top, r.right - r.left, r.bottom - r.top, dpiX));
            }
            return true;
        }

        MonitorEnumProc proc = Callback;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return list;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
