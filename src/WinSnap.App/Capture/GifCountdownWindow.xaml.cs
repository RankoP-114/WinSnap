using System.Windows;
using System.Windows.Interop;
using WinSnap.Interop;

namespace WinSnap.App.Capture;

public partial class GifCountdownWindow : Window
{
    private readonly int _x;
    private readonly int _y;
    private readonly int _width;
    private readonly int _height;

    public GifCountdownWindow(int x, int y, int width, int height)
    {
        InitializeComponent();
        _x = x;
        _y = y;
        _width = width;
        _height = height;
    }

    public static async Task ShowCountdownAsync(
        int x,
        int y,
        int width,
        int height,
        int seconds,
        CancellationToken cancellationToken)
    {
        if (seconds <= 0)
            return;

        var window = new GifCountdownWindow(x, y, width, height);
        window.Show();
        try
        {
            for (int remaining = seconds; remaining >= 1; remaining--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                window.CountdownText.Text = remaining.ToString();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        finally
        {
            window.Close();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        ScreenWindowHelper.PositionTopmost(hwnd, _x, _y, _width, _height);
        Opacity = 1.0;
    }
}
