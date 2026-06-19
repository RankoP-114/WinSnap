namespace WinSnap.Interop;

/// <summary>
/// 覆盖层窗口的物理像素定位与光标坐标工具。
/// 绕开 WPF 在 PerMonitorV2 + 混合 DPI 下 Window.Left/Top 的歧义（dotnet/wpf#4127）。
/// </summary>
public static class ScreenWindowHelper
{
    /// <summary>把窗口精确定位/铺满指定物理像素矩形（用于按显示器覆盖），置顶且不抢焦点。</summary>
    public static void PositionTopmost(
        IntPtr hwnd,
        int x,
        int y,
        int width,
        int height,
        bool showWindow = true,
        bool suppressRedraw = false)
    {
        uint flags = NativeMethods.SWP_NOACTIVATE;
        if (showWindow)
            flags |= NativeMethods.SWP_SHOWWINDOW;
        if (suppressRedraw)
            flags |= NativeMethods.SWP_NOREDRAW;

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

    /// <summary>
    /// 在 WPF/MIL 首帧尚未提交时，用已捕获的 32bpp BGRA/BGRx 背景响应系统擦背景。
    /// 这避免 HDR/SDR 覆盖层从屏外移入时短暂露出 DWM 默认灰底。
    /// </summary>
    public static unsafe void PaintBgra32ToDevice(IntPtr hdc, byte[] pixels, int width, int height)
    {
        if (hdc == IntPtr.Zero)
            return;
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(pixels);

        int requiredBytes = checked(width * height * 4);
        if (pixels.Length < requiredBytes)
            throw new ArgumentException("背景缓冲区太小。", nameof(pixels));

        var bmi = BitmapInfo.CreateTopDownBgra(width, height);
        fixed (byte* bits = pixels)
        {
            StretchDIBits(
                hdc,
                0,
                0,
                width,
                height,
                0,
                0,
                width,
                height,
                bits,
                ref bmi,
                DIB_RGB_COLORS,
                NativeMethods.SRCCOPY);
        }
    }

    public static void PaintBgra32ToWindow(IntPtr hwnd, byte[] pixels, int width, int height)
    {
        if (hwnd == IntPtr.Zero)
            return;

        IntPtr hdc = NativeMethods.GetDC(hwnd);
        if (hdc == IntPtr.Zero)
            return;

        try
        {
            PaintBgra32ToDevice(hdc, pixels, width, height);
        }
        finally
        {
            NativeMethods.ReleaseDC(hwnd, hdc);
        }
    }

    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;

        public static BitmapInfo CreateTopDownBgra(int width, int height)
        {
            int stride = checked(width * 4);
            return new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BI_RGB,
                    SizeImage = checked((uint)((long)stride * height)),
                    XPelsPerMeter = 2835,
                    YPelsPerMeter = 2835,
                },
            };
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern unsafe int StretchDIBits(
        IntPtr hdc,
        int xDest,
        int yDest,
        int destWidth,
        int destHeight,
        int xSrc,
        int ySrc,
        int srcWidth,
        int srcHeight,
        byte* bits,
        ref BitmapInfo bmi,
        uint usage,
        uint rop);
}
