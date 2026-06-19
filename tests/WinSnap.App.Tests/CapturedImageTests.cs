using WinSnap.Interop;

namespace WinSnap.App.Tests;

public class CapturedImageTests
{
    [Fact]
    public void Constructor_DefaultDiagnosticsIsEmpty()
    {
        var image = new CapturedImage(1, 1, [0, 0, 0, 255]);

        Assert.Empty(image.Diagnostics);
    }

    [Fact]
    public void Constructor_CopiesDiagnosticsFromMutableSource()
    {
        var diagnostics = new List<string> { "first" };
        var image = new CapturedImage(1, 1, [0, 0, 0, 255], diagnostics);

        diagnostics[0] = "changed";
        diagnostics.Add("second");

        Assert.Equal(["first"], image.Diagnostics);
    }

    [Fact]
    public void Constructor_CopiesDiagnosticsFromArraySource()
    {
        string[] diagnostics = ["first"];
        var image = new CapturedImage(1, 1, [0, 0, 0, 255], diagnostics);

        diagnostics[0] = "changed";

        Assert.Equal(["first"], image.Diagnostics);
    }

    [Fact]
    public void Constructor_AllowsBufferLargerThanRequired()
    {
        var image = new CapturedImage(1, 1, new byte[8]);

        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(8, image.PixelsBgra.Length);
    }

    [Fact]
    public void Constructor_RejectsUndersizedBuffer()
    {
        Assert.Throws<ArgumentException>(() => new CapturedImage(2, 2, new byte[15]));
    }

    [Fact]
    public void Constructor_RejectsNegativeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapturedImage(-1, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapturedImage(1, -1, []));
    }
}
