using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinSnap.App.Annotation;
using WinSnap.Core.Annotations;
using WinSnap.Interop;

namespace WinSnap.App.Capture;

/// <summary>
/// 单个显示器的截图覆盖窗口：在该屏物理矩形内 1:1 精确渲染（单 DPI，ScaleTransform=1/屏DPI），
/// 把鼠标/键盘事件以绝对物理坐标转发给 <see cref="CaptureSession"/>，并渲染会话全局状态中
/// 落在本屏内的部分（背景、取景框、控制点、标注）。放大镜/工具栏/文字框由会话协调显示。
/// </summary>
public partial class CaptureScreenWindow : Window
{
    private const int WmEraseBackground = 0x0014;
    private const int WmNcCalcSize = 0x0083;
    private static readonly IntPtr EraseBackgroundHandled = new(1);

    private readonly CaptureSession _session;
    private readonly BitmapSource _fullBackground;
    private readonly BitmapSource _normalBackground;
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly RectangleGeometry _selectionClip = new();
    private readonly AnnotationCanvas _annotations = new();
    private double _scale;
    private long _lastAnnotationRevision = -1;
    private bool _firstFrameReported;
    private bool _backgroundDimmed;
    private bool _toolbarPinned;
    private bool _toolbarDragging;
    private Point _toolbarDragStart;
    private double _toolbarStartLeft;
    private double _toolbarStartTop;
    private bool _pointerMoveQueued;
    private Point _queuedPointer;
    private HwndSource? _hwndSource;

    public MonitorInfo Monitor { get; }

    public event EventHandler? FirstFrameRendered;

    public CaptureScreenWindow(CaptureSession session, MonitorInfo monitor, BitmapSource fullBackground)
    {
        InitializeComponent();
        _session = session;
        Monitor = monitor;
        _fullBackground = fullBackground;
        _scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;

        var offscreen = GetOffscreenPosition();
        Left = offscreen.X / _scale;
        Top = offscreen.Y / _scale;
        Width = Monitor.Width / _scale;
        Height = Monitor.Height / _scale;
        ShowActivated = false;

        // 背景：从整屏定格图中裁出本显示器对应区域
        var vs = session.VirtualScreen;
        var background = new CroppedBitmap(fullBackground,
            new Int32Rect(monitor.X - vs.X, monitor.Y - vs.Y, monitor.Width, monitor.Height));
        background.Freeze();
        _normalBackground = background;
        BackgroundImage.Source = _normalBackground;
        SelectionImage.Source = _normalBackground;
        RootCanvas.Width = Monitor.Width;
        RootCanvas.Height = Monitor.Height;
        BackgroundImage.Width = Monitor.Width;
        BackgroundImage.Height = Monitor.Height;
        DimOverlay.Width = Monitor.Width;
        DimOverlay.Height = Monitor.Height;
        SelectionImage.Width = Monitor.Width;
        SelectionImage.Height = Monitor.Height;
        SelectionImage.Clip = _selectionClip;
        RootCanvas.LayoutTransform = new ScaleTransform(1.0 / _scale, 1.0 / _scale);

        // 标注层：共享会话文档，按 -屏原点 偏移把绝对坐标映射到本屏本地坐标
        _annotations.Document = session.Document;
        _annotations.IsHitTestVisible = false;
        _annotations.ClipToBounds = false;
        _annotations.Width = monitor.Width;
        _annotations.Height = monitor.Height;
        _annotations.RenderTransform = new TranslateTransform(-monitor.X, -monitor.Y);
        _annotations.MosaicSource = session.Background;
        _annotations.MosaicOriginX = session.VirtualScreen.X;
        _annotations.MosaicOriginY = session.VirtualScreen.Y;
        RootCanvas.Children.Insert(RootCanvas.Children.IndexOf(SelectionImage) + 1, _annotations);

        CreateHandles();

        RectTool.Click += (_, _) => _session.SelectTool(AnnotationTool.Rectangle);
        EllipseTool.Click += (_, _) => _session.SelectTool(AnnotationTool.Ellipse);
        ArrowTool.Click += (_, _) => _session.SelectTool(AnnotationTool.Arrow);
        PenTool.Click += (_, _) => _session.SelectTool(AnnotationTool.Freehand);
        TextTool.Click += (_, _) => _session.SelectTool(AnnotationTool.Text);
        MosaicTool.Click += (_, _) => _session.SelectTool(AnnotationTool.Mosaic);
        NumberTool.Click += (_, _) => _session.SelectTool(AnnotationTool.Number);
        UndoButton.Click += (_, _) => _session.Undo();
        RedoButton.Click += (_, _) => _session.Redo();
        PinButton.Click += (_, _) => _session.DoPin();
        SaveButton.Click += (_, _) => _session.DoSave();
        CancelButton.Click += (_, _) => _session.Cancel();
        ConfirmButton.Click += (_, _) => _session.DoCopy();

        ConfigureToolbarForMode();

        Toolbar.PreviewMouseLeftButtonDown += OnToolbarMouseLeftButtonDown;
        Toolbar.PreviewMouseMove += OnToolbarMouseMove;
        Toolbar.PreviewMouseLeftButtonUp += OnToolbarMouseLeftButtonUp;

        TextInputBox.KeyDown += OnTextKeyDown;
        TextInputBox.LostFocus += (_, _) => CommitTextInput();
    }

    private void ConfigureToolbarForMode()
    {
        if (_session.IsPinCapture)
        {
            ConfirmButton.ToolTip = "钉到屏幕（双击 / ⏎）";
            return;
        }

        if (!_session.IsSelectionOnlyMode)
            return;

        RectTool.Visibility = Visibility.Collapsed;
        EllipseTool.Visibility = Visibility.Collapsed;
        ArrowTool.Visibility = Visibility.Collapsed;
        PenTool.Visibility = Visibility.Collapsed;
        TextTool.Visibility = Visibility.Collapsed;
        MosaicTool.Visibility = Visibility.Collapsed;
        NumberTool.Visibility = Visibility.Collapsed;
        AnnotationSeparator.Visibility = Visibility.Collapsed;
        UndoButton.Visibility = Visibility.Collapsed;
        RedoButton.Visibility = Visibility.Collapsed;
        ActionSeparator.Visibility = Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;

        if (_session.IsGifCapture)
        {
            SaveButton.ToolTip = "录制 GIF 并保存";
            ConfirmButton.ToolTip = "录制 GIF 并复制（双击 / ⏎）";
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WindowMessageHook);

        var offscreen = GetOffscreenPosition();
        ScreenWindowHelper.PositionTopmost(hwnd, offscreen.X, offscreen.Y, Monitor.Width, Monitor.Height);

        RenderState();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_firstFrameReported)
            return;

        _firstFrameReported = true;
        ScreenWindowHelper.FlushDwm();
        FirstFrameRendered?.Invoke(this, EventArgs.Empty);
    }

    public void Reveal()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            ScreenWindowHelper.PositionTopmost(hwnd, Monitor.X, Monitor.Y, Monitor.Width, Monitor.Height, showWindow: false);
    }

    public void ActivateForInput()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        ScreenWindowHelper.ActivateForInput(hwnd);
        Activate();
        Focus();
    }

    private (int X, int Y) GetOffscreenPosition()
    {
        var vs = _session.VirtualScreen;
        return (vs.X + vs.Width + 8192, Monitor.Y);
    }

    protected override void OnClosed(EventArgs e)
    {
        _pointerMoveQueued = false;
        _hwndSource?.RemoveHook(WindowMessageHook);
        _hwndSource = null;
        BackgroundImage.Source = null;
        SelectionImage.Source = null;
        SelectionImage.Clip = null;
        Magnifier.Clear();
        _annotations.SetPreview(null);
        _annotations.MosaicSource = null;
        _annotations.Document = new AnnotationDocument();
        TextInputBox.Text = string.Empty;
        RootCanvas.LayoutTransform = null;
        base.OnClosed(e);
    }

    private static IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEraseBackground:
                handled = true;
                return EraseBackgroundHandled;
            case WmNcCalcSize:
                handled = true;
                return IntPtr.Zero;
            default:
                return IntPtr.Zero;
        }
    }

    private static Point CursorPoint()
    {
        var (x, y) = ScreenWindowHelper.GetCursorPosition();
        return new Point(x, y);
    }

    // ---------- 输入转发（绝对物理坐标）----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        _session.PointerDown(CursorPoint(), e.ClickCount, this);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _queuedPointer = CursorPoint();

        if (_pointerMoveQueued)
            return;

        _pointerMoveQueued = true;
        Dispatcher.BeginInvoke(new Action(ProcessQueuedPointerMove), DispatcherPriority.Render);
    }

    private void ProcessQueuedPointerMove()
    {
        if (!_pointerMoveQueued)
            return;

        _pointerMoveQueued = false;
        _session.PointerMove(_queuedPointer);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        _session.PointerUp(CursorPoint());
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        _session.RightClick();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (TextInputBox.Visibility == Visibility.Visible)
            return; // 文字输入时按键交给 TextBox
        _session.KeyPressed(e.Key, Keyboard.Modifiers);
    }

    // ---------- 渲染本屏状态 ----------

    public void RenderState()
    {
        var sel = _session.Selection;
        bool active = _session.HasSelection || _session.Mode == CaptureSession.DragMode.Creating;
        bool valid = active && sel.Width > 0 && sel.Height > 0;
        var local = new Rect(sel.X - Monitor.X, sel.Y - Monitor.Y, sel.Width, sel.Height);

        if (valid)
        {
            UpdateSelectionDimmer(local);

            Canvas.SetLeft(SelectionBorder, local.X);
            Canvas.SetTop(SelectionBorder, local.Y);
            SelectionBorder.Width = local.Width;
            SelectionBorder.Height = local.Height;
            SelectionBorder.Visibility = Visibility.Visible;

            SizeText.Text = $"{(int)Math.Round(sel.Width)} × {(int)Math.Round(sel.Height)}";
            double hintY = local.Y - 36;
            if (hintY < 2) hintY = local.Y + 6;
            Canvas.SetLeft(SizeHint, Math.Max(2, local.X));
            Canvas.SetTop(SizeHint, hintY);
            SizeHint.Visibility = Visibility.Visible;
        }
        else
        {
            HideSelectionDimmer();
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeHint.Visibility = Visibility.Collapsed;
        }

        bool showHandles = _session.HasSelection && _session.Mode != CaptureSession.DragMode.Creating && valid;
        var pts = _session.HandlePoints(sel);
        for (int i = 0; i < 8; i++)
        {
            if (showHandles)
            {
                Canvas.SetLeft(_handles[i], pts[i].X - Monitor.X - CaptureSession.HandleSize / 2);
                Canvas.SetTop(_handles[i], pts[i].Y - Monitor.Y - CaptureSession.HandleSize / 2);
                _handles[i].Visibility = Visibility.Visible;
            }
            else
            {
                _handles[i].Visibility = Visibility.Collapsed;
            }
        }

        var hover = _session.HoverWindowRect;
        if (hover is { } hr)
        {
            Canvas.SetLeft(HoverBorder, hr.X - Monitor.X);
            Canvas.SetTop(HoverBorder, hr.Y - Monitor.Y);
            HoverBorder.Width = hr.Width;
            HoverBorder.Height = hr.Height;
            HoverBorder.Visibility = Visibility.Visible;
        }
        else
        {
            HoverBorder.Visibility = Visibility.Collapsed;
        }

        if (_lastAnnotationRevision != _session.AnnotationRevision)
        {
            _annotations.SetPreview(_session.Preview);
            _annotations.Refresh();
            _lastAnnotationRevision = _session.AnnotationRevision;
        }
    }

    private void UpdateSelectionDimmer(Rect localSelection)
    {
        ShowDimmedBackground();

        double left = Math.Clamp(localSelection.Left, 0, Monitor.Width);
        double top = Math.Clamp(localSelection.Top, 0, Monitor.Height);
        double right = Math.Clamp(localSelection.Right, 0, Monitor.Width);
        double bottom = Math.Clamp(localSelection.Bottom, 0, Monitor.Height);

        if (right > left && bottom > top)
        {
            _selectionClip.Rect = new Rect(left, top, right - left, bottom - top);
            if (!ReferenceEquals(SelectionImage.Clip, _selectionClip))
                SelectionImage.Clip = _selectionClip;
            SelectionImage.Visibility = Visibility.Visible;
        }
        else
        {
            SelectionImage.Visibility = Visibility.Collapsed;
        }
    }

    private void HideSelectionDimmer()
    {
        if (_backgroundDimmed)
        {
            DimOverlay.Visibility = Visibility.Collapsed;
            _backgroundDimmed = false;
        }

        SelectionImage.Visibility = Visibility.Collapsed;
    }

    private void ShowDimmedBackground()
    {
        if (_backgroundDimmed)
            return;

        DimOverlay.Visibility = Visibility.Visible;
        _backgroundDimmed = true;
    }

    private void CreateHandles()
    {
        var stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        for (int i = 0; i < 8; i++)
        {
            var rc = new Rectangle
            {
                Width = CaptureSession.HandleSize,
                Height = CaptureSession.HandleSize,
                Fill = Brushes.White,
                Stroke = stroke,
                StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed,
            };
            _handles[i] = rc;
            HandleCanvas.Children.Add(rc);
        }
    }

    // ---------- 放大镜 / 工具栏 / 文字（由会话协调）----------

    public void ShowMagnifier(Point globalCursor)
    {
        var vs = _session.VirtualScreen;
        int bx = (int)globalCursor.X - vs.X, by = (int)globalCursor.Y - vs.Y;
        Magnifier.Update(_fullBackground, bx, by, _session.SamplePixel((int)globalCursor.X, (int)globalCursor.Y));

        double lx = (globalCursor.X - Monitor.X) / _scale, ly = (globalCursor.Y - Monitor.Y) / _scale;
        double mx = lx + 20, my = ly + 20;
        if (mx + 170 > OverlayLayer.ActualWidth) mx = lx - 170;
        if (my + 210 > OverlayLayer.ActualHeight) my = ly - 210;
        Canvas.SetLeft(Magnifier, Math.Max(0, mx));
        Canvas.SetTop(Magnifier, Math.Max(0, my));
        Magnifier.Visibility = Visibility.Visible;
    }

    public void HideMagnifier() => Magnifier.Visibility = Visibility.Collapsed;

    public void ShowToolbar()
    {
        Toolbar.Visibility = Visibility.Visible;
        Toolbar.UpdateLayout();
        double tw = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : 380;
        double th = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : 46;

        if (_toolbarPinned)
        {
            ClampToolbarPosition(tw, th);
            return;
        }

        var sel = _session.Selection;
        double rx = (sel.Right - Monitor.X) / _scale, by = (sel.Bottom - Monitor.Y) / _scale;
        double left = rx - tw, top = by + 8;
        if (left < 4) left = 4;
        if (top + th > OverlayLayer.ActualHeight) top = by - th - 8;
        if (top < 4) top = 4;
        Canvas.SetLeft(Toolbar, Math.Max(4, left));
        Canvas.SetTop(Toolbar, top);
    }

    public void HideToolbar()
    {
        if (_toolbarDragging)
            EndToolbarDrag();
        Toolbar.Visibility = Visibility.Collapsed;
    }

    private void OnToolbarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanStartToolbarDrag(e.OriginalSource as DependencyObject))
            return;

        _toolbarDragging = true;
        _toolbarPinned = true;
        _toolbarDragStart = e.GetPosition(OverlayLayer);
        _toolbarStartLeft = ReadCanvasLeft(Toolbar);
        _toolbarStartTop = ReadCanvasTop(Toolbar);
        Toolbar.CaptureMouse();
        e.Handled = true;
    }

    private void OnToolbarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_toolbarDragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(OverlayLayer);
        double left = _toolbarStartLeft + current.X - _toolbarDragStart.X;
        double top = _toolbarStartTop + current.Y - _toolbarDragStart.Y;
        SetToolbarPosition(left, top);
        e.Handled = true;
    }

    private void OnToolbarMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_toolbarDragging)
            return;

        EndToolbarDrag();
        e.Handled = true;
    }

    private void EndToolbarDrag()
    {
        _toolbarDragging = false;
        Toolbar.ReleaseMouseCapture();
    }

    private bool CanStartToolbarDrag(DependencyObject? source)
    {
        if (source is null)
            return true;

        if (IsDescendantOf(source, ToolbarDragHandle))
            return true;

        while (source is not null && source != Toolbar)
        {
            if (source is ButtonBase or TextBox)
                return false;
            source = VisualTreeHelper.GetParent(source);
        }

        return true;
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private void SetToolbarPosition(double left, double top)
    {
        double tw = Toolbar.ActualWidth > 0 ? Toolbar.ActualWidth : Toolbar.DesiredSize.Width;
        double th = Toolbar.ActualHeight > 0 ? Toolbar.ActualHeight : Toolbar.DesiredSize.Height;
        double maxLeft = Math.Max(4, OverlayLayer.ActualWidth - tw - 4);
        double maxTop = Math.Max(4, OverlayLayer.ActualHeight - th - 4);

        Canvas.SetLeft(Toolbar, Math.Clamp(left, 4, maxLeft));
        Canvas.SetTop(Toolbar, Math.Clamp(top, 4, maxTop));
    }

    private void ClampToolbarPosition(double width, double height)
    {
        double left = ReadCanvasLeft(Toolbar);
        double top = ReadCanvasTop(Toolbar);
        double maxLeft = Math.Max(4, OverlayLayer.ActualWidth - width - 4);
        double maxTop = Math.Max(4, OverlayLayer.ActualHeight - height - 4);

        Canvas.SetLeft(Toolbar, Math.Clamp(left, 4, maxLeft));
        Canvas.SetTop(Toolbar, Math.Clamp(top, 4, maxTop));
    }

    private static double ReadCanvasLeft(UIElement element)
    {
        double value = Canvas.GetLeft(element);
        return double.IsNaN(value) ? 4 : value;
    }

    private static double ReadCanvasTop(UIElement element)
    {
        double value = Canvas.GetTop(element);
        return double.IsNaN(value) ? 4 : value;
    }

    public void SyncToolSelection(AnnotationTool tool)
    {
        RectTool.IsChecked = tool == AnnotationTool.Rectangle;
        EllipseTool.IsChecked = tool == AnnotationTool.Ellipse;
        ArrowTool.IsChecked = tool == AnnotationTool.Arrow;
        PenTool.IsChecked = tool == AnnotationTool.Freehand;
        TextTool.IsChecked = tool == AnnotationTool.Text;
        MosaicTool.IsChecked = tool == AnnotationTool.Mosaic;
        NumberTool.IsChecked = tool == AnnotationTool.Number;
    }

    public void ShowTextInput(Point globalPoint)
    {
        TextInputBox.FontSize = 22 / _scale;
        Canvas.SetLeft(TextInputBox, (globalPoint.X - Monitor.X) / _scale);
        Canvas.SetTop(TextInputBox, (globalPoint.Y - Monitor.Y) / _scale);
        TextInputBox.Text = string.Empty;
        TextInputBox.Visibility = Visibility.Visible;
        TextInputBox.Focus();
    }

    private void OnTextKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTextInput();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            TextInputBox.Visibility = Visibility.Collapsed;
            Focus();
        }
    }

    private void CommitTextInput()
    {
        if (TextInputBox.Visibility != Visibility.Visible)
            return;
        var text = TextInputBox.Text;
        TextInputBox.Visibility = Visibility.Collapsed;
        _session.CommitText(text);
        Focus();
    }
}
