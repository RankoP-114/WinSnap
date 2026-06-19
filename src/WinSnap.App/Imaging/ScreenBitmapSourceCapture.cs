using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSnap.Interop;

namespace WinSnap.App.Imaging;

/// <summary>
/// 覆盖层专用的屏幕位图捕获：用短生命周期 BGRA 缓冲承接 GDI 帧，再复制为 WPF BitmapSource。
/// 避免把 HBITMAP 交给 WPF 持有，也避免大屏截图缓冲被数组池长期保留。
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

        int stride = checked(width * 4);
        int bytes = checked(stride * height);
        byte[] pixels = new byte[bytes];
        GdiCapture.CaptureRegionInto(x, y, width, height, pixels);
        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            pixels,
            stride);
        source.Freeze();
        return source;
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
        byte[] row = new byte[stride];
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
}
