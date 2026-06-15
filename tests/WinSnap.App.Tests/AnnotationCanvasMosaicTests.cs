using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSnap.App.Annotation;
using WinSnap.Core.Annotations;

namespace WinSnap.App.Tests;

public class AnnotationCanvasMosaicTests
{
    [Fact]
    public void RenderMosaicBitmap_BlurUsesBlurPathNotPixelatePath()
    {
        var source = CreateSingleRowBitmap([0, 0, 255, 0, 0]);

        var pixelated = RenderMosaicBitmap(source, MosaicMode.Pixelate);
        var blurred = RenderMosaicBitmap(source, MosaicMode.Blur);

        Assert.NotEqual(ReadRedChannel(pixelated), ReadRedChannel(blurred));
        Assert.Equal([85, 63, 51, 63, 85], ReadRedChannel(blurred));
    }

    private static BitmapSource RenderMosaicBitmap(BitmapSource source, MosaicMode mode)
    {
        var method = typeof(AnnotationCanvas).GetMethod(
            "RenderMosaicBitmap",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return Assert.IsAssignableFrom<BitmapSource>(
            method.Invoke(null, [source, 0, 0, source.PixelWidth, source.PixelHeight, 2, mode]));
    }

    private static BitmapSource CreateSingleRowBitmap(byte[] redValues)
    {
        int width = redValues.Length;
        byte[] bgra = new byte[width * 4];
        for (int x = 0; x < width; x++)
        {
            int i = x * 4;
            bgra[i + 2] = redValues[x];
            bgra[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] ReadRedChannel(BitmapSource source)
    {
        byte[] bgra = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(bgra, source.PixelWidth * 4, 0);
        byte[] red = new byte[source.PixelWidth * source.PixelHeight];
        for (int i = 0; i < red.Length; i++)
            red[i] = bgra[(i * 4) + 2];
        return red;
    }
}
