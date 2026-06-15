using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WinSnap.Interop;

namespace WinSnap.App.Capture;

/// <summary>GIF 录制期间贴在选区外侧的轻量边框提示。</summary>
public sealed class GifCaptureFrameWindow : IDisposable
{
    private const int FrameThicknessPx = 3;
    private readonly List<FrameSegmentWindow> _segments = [];
    private bool _closed;

    public GifCaptureFrameWindow(int captureX, int captureY, int captureWidth, int captureHeight)
    {
        if (captureWidth <= 0 || captureHeight <= 0)
            return;

        AddPreferredSegment(captureX, captureY - FrameThicknessPx, captureWidth, FrameThicknessPx,
            captureX, captureY, captureWidth, FrameThicknessPx);
        AddPreferredSegment(captureX, captureY + captureHeight, captureWidth, FrameThicknessPx,
            captureX, captureY + captureHeight - FrameThicknessPx, captureWidth, FrameThicknessPx);
        AddPreferredSegment(captureX - FrameThicknessPx, captureY, FrameThicknessPx, captureHeight,
            captureX, captureY, FrameThicknessPx, captureHeight);
        AddPreferredSegment(captureX + captureWidth, captureY, FrameThicknessPx, captureHeight,
            captureX + captureWidth - FrameThicknessPx, captureY, FrameThicknessPx, captureHeight);
    }

    public void Show()
    {
        foreach (var segment in _segments)
            segment.Show();
    }

    public void Close()
    {
        if (_closed)
            return;

        _closed = true;
        foreach (var segment in _segments)
            segment.Close();
        _segments.Clear();
    }

    public void Dispose() => Close();

    private void AddPreferredSegment(
        int preferredX,
        int preferredY,
        int preferredWidth,
        int preferredHeight,
        int fallbackX,
        int fallbackY,
        int fallbackWidth,
        int fallbackHeight)
    {
        if (!AddClippedSegment(preferredX, preferredY, preferredWidth, preferredHeight))
            AddClippedSegment(fallbackX, fallbackY, fallbackWidth, fallbackHeight);
    }

    private bool AddClippedSegment(int x, int y, int width, int height)
    {
        var vs = VirtualScreenInfo.Get();
        int left = Math.Max(x, vs.X);
        int top = Math.Max(y, vs.Y);
        int right = Math.Min(x + width, vs.Right);
        int bottom = Math.Min(y + height, vs.Bottom);
        if (right <= left || bottom <= top)
            return false;

        _segments.Add(new FrameSegmentWindow(left, top, right - left, bottom - top));
        return true;
    }

    private sealed class FrameSegmentWindow : Window
    {
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly Border _root;

        public FrameSegmentWindow(int x, int y, int width, int height)
        {
            _x = x;
            _y = y;
            _width = width;
            _height = height;
            _root = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x32, 0xD3, 0xFF)),
                SnapsToDevicePixels = true,
            };

            Content = _root;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            Focusable = false;
            Opacity = 0;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var dpi = VisualTreeHelper.GetDpi(this);
            double scale = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
            _root.Width = _width;
            _root.Height = _height;
            _root.LayoutTransform = new ScaleTransform(1.0 / scale, 1.0 / scale);

            var hwnd = new WindowInteropHelper(this).Handle;
            ScreenWindowHelper.PositionTopmost(hwnd, _x, _y, _width, _height);
            Opacity = 0.95;
        }
    }
}
