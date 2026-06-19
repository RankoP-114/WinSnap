using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSnap.App.Tests;

public class BitmapSourceCreateTests
{
    [Fact]
    public void Create_CopiesPixelArrayData()
    {
        byte[] pixels = [1, 2, 3, 255];
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride: 4);
        bitmap.Freeze();

        pixels[0] = 99;
        pixels[1] = 99;
        pixels[2] = 99;

        byte[] copied = new byte[4];
        bitmap.CopyPixels(copied, 4, 0);

        Assert.Equal([1, 2, 3, 255], copied);
    }

    [Fact]
    public void Create_AcceptsPixelArrayLargerThanRequired()
    {
        byte[] pixels = [1, 2, 3, 255, 99, 99, 99, 99];
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride: 4);
        bitmap.Freeze();

        byte[] copied = new byte[4];
        bitmap.CopyPixels(copied, 4, 0);

        Assert.Equal([1, 2, 3, 255], copied);
    }

    [Fact]
    public void Create_Bgr32CopiesPixelArrayData()
    {
        byte[] pixels = [10, 20, 30, 255];
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            pixels,
            stride: 4);
        bitmap.Freeze();

        pixels[0] = 99;
        pixels[1] = 99;
        pixels[2] = 99;

        byte[] copied = new byte[4];
        bitmap.CopyPixels(copied, 4, 0);

        Assert.Equal([10, 20, 30, 255], copied);
    }
}
