using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSnap.App.ScrollCapture;
using WinSnap.Core.Primitives;

namespace WinSnap.App.Tests;

public class ScrollCaptureServiceTests
{
    [Fact]
    public void ToBitmapSource_UsesBgr32SoUndefinedAlphaIsIgnored()
    {
        var buffer = new PixelBuffer(1, 1);
        buffer.Bgra[0] = 0x33;
        buffer.Bgra[1] = 0x22;
        buffer.Bgra[2] = 0x11;
        buffer.Bgra[3] = 0x00;

        var bitmap = InvokeToBitmapSource(buffer);
        byte[] copied = new byte[4];
        bitmap.CopyPixels(copied, 4, 0);

        Assert.Equal(PixelFormats.Bgr32, bitmap.Format);
        Assert.Equal(0x33, copied[0]);
        Assert.Equal(0x22, copied[1]);
        Assert.Equal(0x11, copied[2]);
    }

    private static BitmapSource InvokeToBitmapSource(PixelBuffer buffer)
    {
        var method = typeof(ScrollCaptureService).GetMethod(
            "ToBitmapSource",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(PixelBuffer)],
            modifiers: null);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<BitmapSource>(method.Invoke(null, [buffer]));
    }
}
