using System.Windows;
using System.Windows.Interop;
using WinSnap.Interop;

namespace WinSnap.App.Capture;

public partial class GifRecordingStatusWindow : Window
{
    private const int StatusWidth = 180;
    private const int StatusHeight = 44;
    private const int MarginPx = 8;

    private readonly int _x;
    private readonly int _y;

    public GifRecordingStatusWindow(int captureX, int captureY, int captureWidth, int captureHeight)
    {
        InitializeComponent();
        (_x, _y) = ChoosePosition(captureX, captureY, captureWidth, captureHeight);
    }

    public void SetRemainingSeconds(int seconds)
    {
        StatusText.Text = seconds > 0
            ? $"录制中  {seconds}s"
            : "正在保存";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        ScreenWindowHelper.PositionTopmost(hwnd, _x, _y, StatusWidth, StatusHeight);
        Opacity = 1.0;
    }

    private static (int X, int Y) ChoosePosition(int captureX, int captureY, int captureWidth, int captureHeight)
    {
        var vs = VirtualScreenInfo.Get();
        int x = Math.Clamp(captureX, vs.X, Math.Max(vs.X, vs.Right - StatusWidth));

        if (captureY - StatusHeight - MarginPx >= vs.Y)
            return (x, captureY - StatusHeight - MarginPx);

        if (captureY + captureHeight + MarginPx + StatusHeight <= vs.Bottom)
            return (x, captureY + captureHeight + MarginPx);

        // 全屏选区没有外侧空间时只能放在屏幕右上角；这种场景会被录入，这是物理限制。
        return (Math.Max(vs.X, vs.Right - StatusWidth - MarginPx), vs.Y + MarginPx);
    }
}
