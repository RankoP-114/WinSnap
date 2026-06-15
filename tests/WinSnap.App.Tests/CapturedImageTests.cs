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
}
