using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSnap.App.Pin;
using WinSnap.Interop;

namespace WinSnap.App.Tests;

public class PinManagerTests
{
    [Fact]
    public void PinForTesting_ClosesOldestWindowWhenCapacityIsExceeded()
    {
        var created = new List<FakePinWindowHandle>();
        var manager = new PinManager((_, _, _, _) => CreateFake(created))
        {
            MaxPinnedWindows = 2,
        };
        var image = CreateBitmap();

        manager.PinForTesting(image, null, 0, 0);
        manager.PinForTesting(image, null, 0, 0);
        manager.PinForTesting(image, null, 0, 0);

        Assert.Equal(2, manager.Count);
        Assert.True(created[0].Closed);
        Assert.Equal(1, created[0].CloseCount);
        Assert.False(created[1].Closed);
        Assert.False(created[2].Closed);
    }

    [Fact]
    public void PinForTesting_UnlimitedCapacityDoesNotCloseOldWindows()
    {
        var created = new List<FakePinWindowHandle>();
        var manager = new PinManager((_, _, _, _) => CreateFake(created))
        {
            MaxPinnedWindows = 0,
        };
        var image = CreateBitmap();

        manager.PinForTesting(image, null, 0, 0);
        manager.PinForTesting(image, null, 0, 0);
        manager.PinForTesting(image, null, 0, 0);

        Assert.Equal(3, manager.Count);
        Assert.All(created, window => Assert.False(window.Closed));
    }

    [Fact]
    public void PinClosed_RemovesWindowFromManager()
    {
        var created = new List<FakePinWindowHandle>();
        var manager = new PinManager((_, _, _, _) => CreateFake(created));

        manager.PinForTesting(CreateBitmap(), null, 0, 0);
        created[0].RaisePinClosed();

        Assert.Equal(0, manager.Count);
    }

    [Fact]
    public void CloseAll_ClosesTrackedWindowsAndClearsList()
    {
        var created = new List<FakePinWindowHandle>();
        var manager = new PinManager((_, _, _, _) => CreateFake(created));
        var image = CreateBitmap();

        manager.PinForTesting(image, null, 0, 0);
        manager.PinForTesting(image, null, 0, 0);
        manager.CloseAll();

        Assert.Equal(0, manager.Count);
        Assert.All(created, window => Assert.True(window.Closed));
    }

    private static FakePinWindowHandle CreateFake(List<FakePinWindowHandle> created)
    {
        var window = new FakePinWindowHandle();
        created.Add(window);
        return window;
    }

    private static BitmapSource CreateBitmap()
    {
        byte[] bgra = [0, 0, 0, 255];
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, bgra, 4);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class FakePinWindowHandle : PinManager.IPinWindowHandle
    {
        public event EventHandler? PinClosed;

        public bool Shown { get; private set; }

        public bool Closed { get; private set; }

        public int CloseCount { get; private set; }

        public void Show() => Shown = true;

        public void Close()
        {
            Closed = true;
            CloseCount++;
            PinClosed?.Invoke(this, EventArgs.Empty);
        }

        public void RaisePinClosed()
            => PinClosed?.Invoke(this, EventArgs.Empty);
    }
}
