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
            throw new InvalidOperationException("GetDC 失败。");

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
                throw new InvalidOperationException("CreateCompatibleDC 失败。");

            hBitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (hBitmap == IntPtr.Zero)
                throw new InvalidOperationException("CreateCompatibleBitmap 失败。");

            oldObj = SelectObject(memDc, hBitmap);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException("BitBlt 失败。");

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
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static bool HasVisibleContent(BitmapSource source, byte threshold = 6, int maxSamples = 4096, int requiredHits = 8)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0 || source.Format.BitsPerPixel < 24)
            return false;

        int pixelCount = source.PixelWidth * source.PixelHeight;
        int step = Math.Max(1, pixelCount / Math.Max(1, maxSamples));
        int bytesPerPixel = Math.Max(4, (source.Format.BitsPerPixel + 7) / 8);
        var pixel = new byte[bytesPerPixel];
        int hits = 0;

        for (int p = 0; p < pixelCount; p += step)
        {
            int x = p % source.PixelWidth;
            int y = p / source.PixelWidth;
            source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, bytesPerPixel, 0);
            byte b = pixel[0];
            byte g = pixel[1];
            byte r = pixel[2];
            if (Math.Max(r, Math.Max(g, b)) > threshold && ++hits >= requiredHits)
                return true;
        }

        return false;
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy,
        IntPtr hdcSrc, int x1, int y1, uint rop);
}
