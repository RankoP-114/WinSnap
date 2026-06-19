using WinSnap.Interop;

namespace WinSnap.App.Tests;

public class DuplicationCaptureSessionTests
{
    [Fact]
    public void Capture_ReturnsIndependentPixelBuffers()
    {
        using var session = CreateEmptySession();

        var first = session.Capture();
        var second = session.Capture();

        Assert.NotSame(first.PixelsBgra, second.PixelsBgra);
    }

    [Fact]
    public void CaptureReusable_ReusesPixelBufferWhenNoFrameIsProtected()
    {
        using var session = CreateEmptySession();

        var first = session.CaptureReusable(protectedFrame: null);
        var second = session.CaptureReusable(protectedFrame: null);

        Assert.Same(first.PixelsBgra, second.PixelsBgra);
    }

    [Fact]
    public void CaptureReusable_AvoidsProtectedPixelBuffer()
    {
        using var session = CreateEmptySession();

        var first = session.CaptureReusable(protectedFrame: null);
        var second = session.CaptureReusable(first);
        var firstAgain = session.CaptureReusable(second);

        Assert.NotSame(first.PixelsBgra, second.PixelsBgra);
        Assert.Same(first.PixelsBgra, firstAgain.PixelsBgra);
    }

    private static DuplicationCapture.RegionCaptureSession CreateEmptySession()
        => new(
            x: 0,
            y: 0,
            width: 1,
            height: 1,
            captureSdrWithDesktopDuplication: false,
            sdrAcquireTotalBudgetMs: 1,
            hdrSdrWhiteNits: 300,
            hdrPeakNits: 1000,
            outputs: [],
            constructionDiagnostics: []);
}
