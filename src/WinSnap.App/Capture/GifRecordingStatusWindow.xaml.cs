using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinSnap.Interop;

namespace WinSnap.App.Capture;

public partial class GifRecordingStatusWindow : Window
{
    private const int StopHotkeyId = 0x4749;
    private const int StatusWidth = 230;
    private const int StatusHeight = 44;
    private const int MarginPx = 8;

    private readonly int _captureX;
    private readonly int _captureY;
    private readonly int _captureWidth;
    private readonly int _captureHeight;
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private bool _escapeHotkeyRegistered;
    private int _durationSeconds;

    public event Action? StopRequested;

    public GifRecordingStatusWindow(int captureX, int captureY, int captureWidth, int captureHeight)
    {
        InitializeComponent();
        _captureX = captureX;
        _captureY = captureY;
        _captureWidth = captureWidth;
        _captureHeight = captureHeight;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _timer.Tick += (_, _) => UpdateCountdownFromClock();
        StopButton.Click += (_, _) => StopRequested?.Invoke();
    }

    public void StartCountdown(int durationSeconds)
    {
        _durationSeconds = Math.Max(1, durationSeconds);
        _stopwatch.Restart();
        SetRemainingSeconds(_durationSeconds);
        _timer.Start();
    }

    public void SetRemainingSeconds(int seconds)
    {
        StatusText.Text = seconds > 0
            ? $"剩余  {seconds}s"
            : "正在保存";
    }

    public void SetSaving()
    {
        _timer.Stop();
        SetRemainingSeconds(0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dpi = VisualTreeHelper.GetDpi(this);
        double scale = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
        int physicalWidth = Math.Max(1, (int)Math.Round(StatusWidth * scale));
        int physicalHeight = Math.Max(1, (int)Math.Round(StatusHeight * scale));

        Root.Width = physicalWidth;
        Root.Height = physicalHeight;
        Root.LayoutTransform = new ScaleTransform(1.0 / scale, 1.0 / scale);

        var (x, y) = ChoosePosition(
            _captureX,
            _captureY,
            _captureWidth,
            _captureHeight,
            physicalWidth,
            physicalHeight);
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwnd = hwnd;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        _escapeHotkeyRegistered = GlobalHotkey.Register(
            hwnd,
            StopHotkeyId,
            GlobalHotkey.ModNoRepeat,
            (uint)KeyInterop.VirtualKeyFromKey(Key.Escape));

        ScreenWindowHelper.PositionTopmost(hwnd, x, y, physicalWidth, physicalHeight);
        Opacity = 1.0;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            StopRequested?.Invoke();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (_escapeHotkeyRegistered && _hwnd != IntPtr.Zero)
            GlobalHotkey.Unregister(_hwnd, StopHotkeyId);
        _escapeHotkeyRegistered = false;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        StopRequested = null;
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == GlobalHotkey.WmHotkey && wParam.ToInt32() == StopHotkeyId)
        {
            handled = true;
            StopRequested?.Invoke();
        }

        return IntPtr.Zero;
    }

    private void UpdateCountdownFromClock()
    {
        if (_durationSeconds <= 0)
            return;

        int remaining = Math.Max(
            0,
            (int)Math.Ceiling(_durationSeconds - _stopwatch.Elapsed.TotalSeconds));
        SetRemainingSeconds(remaining);
        if (remaining <= 0)
            _timer.Stop();
    }

    private static (int X, int Y) ChoosePosition(
        int captureX,
        int captureY,
        int captureWidth,
        int captureHeight,
        int statusWidth,
        int statusHeight)
    {
        var vs = VirtualScreenInfo.Get();
        int x = Math.Clamp(captureX, vs.X, Math.Max(vs.X, vs.Right - statusWidth));

        if (captureY - statusHeight - MarginPx >= vs.Y)
            return (x, captureY - statusHeight - MarginPx);

        if (captureY + captureHeight + MarginPx + statusHeight <= vs.Bottom)
            return (x, captureY + captureHeight + MarginPx);

        // 全屏选区没有外侧空间时只能放在屏幕右上角；这种场景会被录入，这是物理限制。
        return (Math.Max(vs.X, vs.Right - statusWidth - MarginPx), vs.Y + MarginPx);
    }
}
