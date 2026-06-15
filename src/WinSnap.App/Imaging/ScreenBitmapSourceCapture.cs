using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WinSnap.Interop;

namespace WinSnap.App.Imaging;

/// <summary>
/// 覆盖层专用的屏幕位图捕获：直接把 GDI HBITMAP 转为 WPF BitmapSource，
/// 避免先构造一份整屏托管 BGRA byte[] 再复制给 WPF。
/// </summary>
public static class ScreenBitmapSourceCapture
{
    public static BitmapSource CaptureVirtualScreenGdi()
    {
        var vs = VirtualScreenInfo.Get();
        return CaptureRegionGdi(vs.X, vs.Y, vs.Width, vs.Height);
    }

    public static BitmapSource CaptureRegionGdi(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "捕获区域必须大于 0。");

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            ThrowLastWin32("GetDC");

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
                ThrowLastWin32("CreateCompatibleDC");

            hBitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (hBitmap == IntPtr.Zero)
                ThrowLastWin32("CreateCompatibleBitmap");

            oldObj = SelectObject(memDc, hBitmap);
            if (oldObj == IntPtr.Zero || oldObj == new IntPtr(-1))
                ThrowLastWin32("SelectObject");
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                ThrowLastWin32("BitBlt");

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero)
                SelectObject(memDc, oldObj);
            if (hBitmap != IntPtr.Zero)
                DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero)
                DeleteDC(memDc);
            if (screenDc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static bool HasVisibleContent(BitmapSource source, byte threshold = 6, int maxSamples = 4096, int requiredHits = 8)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0 || source.Format.BitsPerPixel < 24)
            return false;

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        long pixelCount = (long)width * height;
        long step = Math.Max(1, pixelCount / Math.Max(1, maxSamples));
        int bitsPerPixel = source.Format.BitsPerPixel;
        int bytesPerPixel = Math.Max(3, (bitsPerPixel + 7) / 8);
        int stride = checked(((width * bitsPerPixel) + 7) / 8);
        var row = new byte[stride];
        int copiedY = -1;
        int hits = 0;

        for (long p = 0; p < pixelCount; p += step)
        {
            int x = (int)(p % width);
            int y = (int)(p / width);
            if (y != copiedY)
            {
                source.CopyPixels(new Int32Rect(0, y, width, 1), row, stride, 0);
                copiedY = y;
            }

            int offset = x * bytesPerPixel;
            byte b = row[offset];
            byte g = row[offset + 1];
            byte r = row[offset + 2];
            if (Math.Max(r, Math.Max(g, b)) > threshold && ++hits >= requiredHits)
                return true;
        }

        return false;
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    private static void ThrowLastWin32(string api)
    {
        int error = Marshal.GetLastWin32Error();
        throw new InvalidOperationException($"{api} 失败：{new Win32Exception(error).Message} (0x{error:X8})");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy,
        IntPtr hdcSrc, int x1, int y1, uint rop);
}
