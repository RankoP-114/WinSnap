using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinSnap.Interop;

/// <summary>
/// 基于 GDI BitBlt 的屏幕捕获（SDR 路径）。
/// 使用 <c>SRCCOPY | CAPTUREBLT</c> 以正确抓取 layered（半透明）窗口。
/// HDR 显示器请改用 Desktop Duplication 路径（见 M4）。
/// </summary>
public static class GdiCapture
{
    /// <summary>抓取整个虚拟桌面（所有显示器）。</summary>
    public static CapturedImage CaptureVirtualScreen()
    {
        var vs = VirtualScreenInfo.Get();
        return CaptureRegion(vs.X, vs.Y, vs.Width, vs.Height);
    }

    /// <summary>抓取虚拟桌面坐标系下的指定物理像素矩形。</summary>
    public static CapturedImage CaptureRegion(int x, int y, int width, int height)
    {
        var buffer = new byte[GetRequiredBufferSize(width, height)];
        CaptureRegionInto(x, y, width, height, buffer);
        return new CapturedImage(width, height, buffer);
    }

    /// <summary>抓取虚拟桌面坐标系下的指定物理像素矩形，直接写入调用方提供的 BGRA 缓冲。</summary>
    public static void CaptureRegionInto(int x, int y, int width, int height, byte[] destinationBgra)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(destinationBgra);
        int requiredBytes = GetRequiredBufferSize(width, height);
        if (destinationBgra.Length < requiredBytes)
            throw new ArgumentException(
                $"目标缓冲区太小，至少需要 {requiredBytes} 字节。", nameof(destinationBgra));

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            ThrowLastWin32("GetDC");

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
                ThrowLastWin32("CreateCompatibleDC");

            var bmi = BitmapInfo.CreateTopDownBgra(width, height);
            hBitmap = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero)
                ThrowLastWin32("CreateDIBSection");

            oldObj = NativeMethods.SelectObject(memDc, hBitmap);
            if (oldObj == IntPtr.Zero || oldObj == new IntPtr(-1))
                ThrowLastWin32("SelectObject");

            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, x, y,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                ThrowLastWin32("BitBlt");
            }

            int stride = checked(width * 4);
            Marshal.Copy(bits, destinationBgra, 0, checked(stride * height));
        }
        finally
        {
            if (memDc != IntPtr.Zero && oldObj != IntPtr.Zero && oldObj != new IntPtr(-1))
                NativeMethods.SelectObject(memDc, oldObj);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero)
                NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero)
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static int GetRequiredBufferSize(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        return checked(width * height * 4);
    }

    private static void ThrowLastWin32(string api)
    {
        int error = Marshal.GetLastWin32Error();
        throw new InvalidOperationException($"{api} 失败：{new Win32Exception(error).Message} (0x{error:X8})");
    }

    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    [StructLayout(LayoutKind.Sequential)]
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

    [StructLayout(LayoutKind.Sequential)]
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
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BitmapInfo pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);
}
