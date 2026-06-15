using System.Reflection;
using System.IO;
using System.Windows.Media.Imaging;
using WinSnap.App.Capture;
using WinSnap.Interop;

namespace WinSnap.App.Tests;

public class GifCaptureServiceEncodingTests
{
    [Fact]
    public void QuantizeLookup_MatchesExpectedIntegerFormula()
    {
        Type encoderType = GetEncoderType();
        var buildLookup = encoderType.GetMethod("BuildQuantizeLookup", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(buildLookup);

        var redLookup = Assert.IsType<byte[]>(buildLookup.Invoke(null, [7]));
        var blueLookup = Assert.IsType<byte[]>(buildLookup.Invoke(null, [3]));

        Assert.Equal(256, redLookup.Length);
        Assert.Equal(256, blueLookup.Length);
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal((byte)(i * 7 / 255), redLookup[i]);
            Assert.Equal((byte)(i * 3 / 255), blueLookup[i]);
        }
    }

    [Fact]
    public void StreamingGifEncoder_WritesGifThatDecodesAcrossLzwCodeSizeBoundary()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winsnap-gif-test-{Guid.NewGuid():N}.gif");
        try
        {
            var image = CreateQuantizedStressImage(width: 1400, height: 1);
            using (var encoder = CreateEncoder(path, image.Width, image.Height))
            {
                Invoke(encoder, "WriteFrame", image, 12);
            }

            var decoder = new GifBitmapDecoder(
                new Uri(path),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            Assert.Single(decoder.Frames);
            Assert.Equal(image.Width, decoder.Frames[0].PixelWidth);
            Assert.Equal(image.Height, decoder.Frames[0].PixelHeight);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static CapturedImage CreateQuantizedStressImage(int width, int height)
    {
        byte[] bgra = new byte[checked(width * height * 4)];
        uint state = 0xC0FFEEu;
        for (int p = 0; p < width * height; p++)
        {
            state = (state * 1664525u) + 1013904223u;
            int index = (int)(state >> 24);
            int r = ((index >> 5) & 0x07) * 255 / 7;
            int g = ((index >> 2) & 0x07) * 255 / 7;
            int b = (index & 0x03) * 255 / 3;
            int offset = p * 4;
            bgra[offset] = (byte)b;
            bgra[offset + 1] = (byte)g;
            bgra[offset + 2] = (byte)r;
            bgra[offset + 3] = 255;
        }

        return new CapturedImage(width, height, bgra);
    }

    private static IDisposable CreateEncoder(string path, int width, int height)
    {
        var encoder = Activator.CreateInstance(
            GetEncoderType(),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [path, width, height],
            culture: null);
        return Assert.IsAssignableFrom<IDisposable>(encoder);
    }

    private static void Invoke(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
    }

    private static Type GetEncoderType()
    {
        var type = typeof(GifCaptureService).GetNestedType("StreamingGifEncoder", BindingFlags.NonPublic);
        Assert.NotNull(type);
        return type;
    }
}
