using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Serilog;
using WinSnap.App.Annotation;
using WinSnap.App.Diagnostics;
using WinSnap.App.Imaging;
using WinSnap.Core.Annotations;
using WinSnap.Core.Commands;
using WinSnap.Core.Primitives;
using WinSnap.Interop;

namespace WinSnap.App.Capture;

/// <summary>
/// 一次截图会话的协调器：持有全局状态（绝对物理像素坐标）与全部交互逻辑，
/// 为每个显示器创建一个 <see cref="CaptureScreenWindow"/>（各自在单一 DPI 内 1:1 精确渲染），
/// 并在各窗口之间协调选区、标注、放大镜与工具栏。彻底解决混合 DPI 跨屏错位问题。
/// </summary>
public sealed class CaptureSession
{
    public enum DragMode { None, Creating, Moving, Resizing }
    public enum SessionMode { Screenshot, PinCapture, LongCapture, GifCapture }
    public enum GifOutputMode { Copy, Save }

    public const double HandleSize = 12;
    private const double MinSelection = 8;
    private const double DefaultThickness = 3;
    private const double TextFontSize = 22;
    private static readonly ColorRgba DefaultStroke = ColorRgba.FromRgb(0xFF, 0x3B, 0x30);

    private readonly BitmapSource _background;
    private readonly VirtualScreenInfo _vs;
    private readonly List<CaptureScreenWindow> _windows = new();
    private readonly UndoRedoStack _undo = new();
    private readonly byte[] _samplePixelBuffer = new byte[4];

    private Point _dragStart;
    private Rect _selectionAtStart;
    private bool _drawingAnnotation;
    private PointInt _annoStart;
    private FreehandAnnotation? _freehand;
    private PointInt _textPos;
    private CaptureScreenWindow? _textWindow;
    private int _nextNumber = 1;
    private IReadOnlyList<WindowEnumerator.WindowBounds> _topLevelWindows = Array.Empty<WindowEnumerator.WindowBounds>();
    private WindowEnumerator.WindowBounds? _hoverWindow;
    private WindowEnumerator.WindowBounds? _downHoverWindow;
    private Point _lastCursor;
    private bool _closed;
    private int _firstFrameReadyCount;
    private bool _revealed;

    private readonly SessionMode _sessionMode;
    private readonly string _defaultSaveFormat;
    private readonly int _jpegQuality;

    public CaptureSession(
        CapturedImage captured,
        SessionMode sessionMode = SessionMode.Screenshot,
        string defaultSaveFormat = "png",
        int jpegQuality = 90)
        : this(ImagingHelper.ToBitmapSource(captured), sessionMode, defaultSaveFormat, jpegQuality)
    {
    }

    public CaptureSession(
        BitmapSource background,
        SessionMode sessionMode = SessionMode.Screenshot,
        string defaultSaveFormat = "png",
        int jpegQuality = 90)
    {
        ArgumentNullException.ThrowIfNull(background);
        _background = background;
        _vs = VirtualScreenInfo.Get();
        _sessionMode = sessionMode;
        _defaultSaveFormat = NormalizeSaveFormat(defaultSaveFormat);
        _jpegQuality = Math.Clamp(jpegQuality, 1, 100);
    }

    public AnnotationDocument Document { get; } = new();
    public BitmapSource Background => _background;
    public VirtualScreenInfo VirtualScreen => _vs;
    public bool IsPinCapture => _sessionMode == SessionMode.PinCapture;
    public bool IsGifCapture => _sessionMode == SessionMode.GifCapture;
    public bool IsSelectionOnlyMode => _sessionMode is SessionMode.LongCapture or SessionMode.GifCapture;

    public Rect Selection { get; private set; }
    public bool HasSelection { get; private set; }
    public DragMode Mode { get; private set; }
    public int ActiveHandle { get; private set; } = -1;
    public AnnotationTool Tool { get; private set; }
    public AnnotationElement? Preview { get; private set; }
    public long AnnotationRevision { get; private set; }

    /// <summary>当前悬停命中的窗口边界（绝对物理像素），仅空闲态（无选区、无工具）有效，供窗口高亮。</summary>
    public Rect? HoverWindowRect => _hoverWindow is { } w && !HasSelection && Mode == DragMode.None
        ? new Rect(w.X, w.Y, w.Width, w.Height) : null;

    public event Action? Closed;

    /// <summary>请求把选区钉到屏幕（合成图、原始裁剪、选区左上物理坐标）。</summary>
    public event Action<BitmapSource, CapturedImage, int, int>? PinRequested;

    /// <summary>长截图模式下确认选区（选区物理矩形 x,y,w,h）。</summary>
    public event Action<int, int, int, int>? LongCaptureConfirmed;

    /// <summary>GIF 录制模式下确认选区（选区物理矩形 x,y,w,h + 输出行为）。</summary>
    public event Action<int, int, int, int, GifOutputMode>? GifCaptureConfirmed;

    public void Start()
    {
        ScreenWindowHelper.ReleaseCursorConstraints();

        // 覆盖层显示前枚举可见顶层窗口，供"悬停高亮窗口"命中（避免命中覆盖层自身）
        _topLevelWindows = WindowEnumerator.EnumerateTopLevelWindows();

        var monitors = MonitorEnumerator.GetMonitors();
        if (monitors.Count == 0)
            monitors = new List<MonitorInfo> { new(_vs.X, _vs.Y, _vs.Width, _vs.Height, 96) };

        foreach (var m in monitors)
        {
            var window = new CaptureScreenWindow(this, m, _background);
            window.FirstFrameRendered += OnWindowFirstFrameRendered;
            window.Closed += OnCaptureWindowClosed;
            _windows.Add(window);
        }

        for (int i = 0; i < _windows.Count; i++)
            _windows[i].Show();

        RenderAll();
        Log.Information("截图会话开始：{Count} 个显示器", _windows.Count);
    }

    private void OnCaptureWindowClosed(object? sender, EventArgs e)
        => Cancel();

    private void OnWindowFirstFrameRendered(object? sender, EventArgs e)
    {
        if (_revealed)
            return;

        _firstFrameReadyCount++;
        if (_firstFrameReadyCount < _windows.Count)
            return;

        _revealed = true;
        foreach (var window in _windows)
        {
            window.FirstFrameRendered -= OnWindowFirstFrameRendered;
            window.Reveal();
        }

        var inputWindow = WindowUnderCursor() ?? _windows.FirstOrDefault();
        inputWindow?.ActivateForInput();

        var (x, y) = ScreenWindowHelper.GetCursorPosition();
        PointerMove(new Point(x, y));
    }

    private CaptureScreenWindow? WindowUnderCursor()
    {
        var (x, y) = ScreenWindowHelper.GetCursorPosition();
        return _windows.FirstOrDefault(w =>
            x >= w.Monitor.X && x < w.Monitor.Right &&
            y >= w.Monitor.Y && y < w.Monitor.Bottom);
    }

    // ---------- 输入（全部为绝对物理像素坐标）----------

    public void PointerDown(Point p, int clickCount, CaptureScreenWindow source)
    {
        bool inSelection = HasSelection && Selection.Contains(p);

        if (Tool == AnnotationTool.Text && inSelection)
        {
            _textPos = ToPi(p);
            _textWindow = source;
            source.ShowTextInput(p);
            return;
        }
        if (Tool == AnnotationTool.Number && inSelection)
        {
            PlaceNumber(p);
            return;
        }
        if (clickCount == 2 && Tool == AnnotationTool.None && inSelection)
        {
            DoCopy();
            return;
        }
        if (Tool is not AnnotationTool.None and not AnnotationTool.Text and not AnnotationTool.Number && inSelection)
        {
            BeginAnnotation(p);
            return;
        }

        _dragStart = p;
        _selectionAtStart = Selection;
        _downHoverWindow = _hoverWindow; // 记录按下时悬停的窗口（用于单击选窗）
        if (HasSelection)
        {
            int handle = HitTestHandle(p);
            if (handle >= 0) { Mode = DragMode.Resizing; ActiveHandle = handle; }
            else if (inSelection) { Mode = DragMode.Moving; }
            else { Mode = DragMode.Creating; HasSelection = false; Selection = new Rect(p, p); }
        }
        else
        {
            Mode = DragMode.Creating;
            Selection = new Rect(p, p);
        }
        RenderAll();
    }

    public void PointerMove(Point p)
    {
        _lastCursor = p;
        var previousSelection = Selection;
        var previousHoverWindow = _hoverWindow;

        // 空闲态：高亮鼠标下的窗口（QQ 风格窗口选取）
        _hoverWindow = (Mode == DragMode.None && !HasSelection && Tool == AnnotationTool.None && !_drawingAnnotation)
            ? FindWindowAt(p)
            : null;

        if (_drawingAnnotation)
        {
            var cur = ToPi(ClampToSelection(p));
            if (Tool == AnnotationTool.Freehand) _freehand!.Points.Add(cur);
            else Preview = CreateShape(Tool, _annoStart, cur);
            MarkAnnotationChanged();
            RenderAll();
            UpdateMagnifier(p);
            return;
        }

        switch (Mode)
        {
            case DragMode.Creating:
                Selection = ClipToBounds(MakeRect(_dragStart, p));
                break;
            case DragMode.Moving:
                Selection = ClampMove(new Rect(
                    _selectionAtStart.X + (p.X - _dragStart.X),
                    _selectionAtStart.Y + (p.Y - _dragStart.Y),
                    _selectionAtStart.Width, _selectionAtStart.Height));
                break;
            case DragMode.Resizing:
                Selection = ResizeSelection(_selectionAtStart, ActiveHandle, p);
                break;
        }
        if (!SameRect(previousSelection, Selection) || !Nullable.Equals(previousHoverWindow, _hoverWindow))
            RenderAll();
        UpdateMagnifier(p);
    }

    public void PointerUp(Point p)
    {
        if (_drawingAnnotation)
        {
            _drawingAnnotation = false;
            var element = Preview;
            Preview = null;
            _freehand = null;
            if (element is not null && IsValidAnnotation(element))
                _undo.Execute(new AddElementCommand(Document, element));
            MarkAnnotationChanged();
            RenderAll();
            return;
        }

        if (Mode == DragMode.Creating)
        {
            bool dragged = Selection.Width >= MinSelection && Selection.Height >= MinSelection;
            if (!dragged && _downHoverWindow is { } wb)
            {
                // 单击未拖动且命中窗口：选区取该窗口边界
                Selection = ClipToBounds(new Rect(wb.X, wb.Y, wb.Width, wb.Height));
                HasSelection = Selection.Width >= MinSelection && Selection.Height >= MinSelection;
            }
            else
            {
                HasSelection = dragged;
            }
            _downHoverWindow = null;
        }
        else if (Mode is DragMode.Moving or DragMode.Resizing)
            HasSelection = true;

        Mode = DragMode.None;
        ActiveHandle = -1;
        RenderAll();
    }

    public void RightClick() => Cancel();

    public void KeyPressed(Key key, ModifierKeys modifiers)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        switch (key)
        {
            case Key.Escape: Cancel(); break;
            case Key.Enter when HasSelection && Mode == DragMode.None && !_drawingAnnotation: DoCopy(); break;
            case Key.Z when ctrl:
                if (_undo.Undo()) { MarkAnnotationChanged(); RenderAll(); }
                break;
            case Key.Y when ctrl:
                if (_undo.Redo()) { MarkAnnotationChanged(); RenderAll(); }
                break;
            case Key.C when !ctrl && (!HasSelection || Mode == DragMode.Creating || Mode == DragMode.Resizing):
                CopyColor((modifiers & ModifierKeys.Shift) != 0);
                break;
        }
    }

    public void SelectTool(AnnotationTool tool)
    {
        Tool = Tool == tool ? AnnotationTool.None : tool;
        foreach (var w in _windows) w.SyncToolSelection(Tool);
    }

    public void Undo() { if (_undo.Undo()) { MarkAnnotationChanged(); RenderAll(); } }
    public void Redo() { if (_undo.Redo()) { MarkAnnotationChanged(); RenderAll(); } }

    public void CommitText(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var annotation = new TextAnnotation
            {
                Position = _textPos,
                Text = text.Trim(),
                FontSize = TextFontSize,
                Stroke = DefaultStroke,
                Thickness = 1,
            };
            _undo.Execute(new AddElementCommand(Document, annotation));
            MarkAnnotationChanged();
            RenderAll();
        }
        _textWindow = null;
    }

    // ---------- 标注绘制 ----------

    private void BeginAnnotation(Point p)
    {
        _drawingAnnotation = true;
        _annoStart = ToPi(ClampToSelection(p));
        if (Tool == AnnotationTool.Freehand)
        {
            _freehand = new FreehandAnnotation
            {
                Stroke = DefaultStroke,
                Thickness = DefaultThickness,
                Points = new List<PointInt> { _annoStart },
            };
            Preview = _freehand;
        }
        else
        {
            Preview = CreateShape(Tool, _annoStart, _annoStart);
        }
        MarkAnnotationChanged();
        RenderAll();
    }

    private void PlaceNumber(Point p)
    {
        var stamp = new NumberStampAnnotation
        {
            Center = ToPi(ClampToSelection(p)),
            Number = _nextNumber++,
            Radius = 14,
            Stroke = DefaultStroke,
            Thickness = 1,
        };
        _undo.Execute(new AddElementCommand(Document, stamp));
        MarkAnnotationChanged();
        RenderAll();
    }

    private void MarkAnnotationChanged()
    {
        unchecked { AnnotationRevision++; }
    }

    private static AnnotationElement CreateShape(AnnotationTool tool, PointInt a, PointInt b) => tool switch
    {
        AnnotationTool.Rectangle => new RectangleAnnotation { Rect = RectInt.FromPoints(a, b), Stroke = DefaultStroke, Thickness = DefaultThickness },
        AnnotationTool.Ellipse => new EllipseAnnotation { Rect = RectInt.FromPoints(a, b), Stroke = DefaultStroke, Thickness = DefaultThickness },
        AnnotationTool.Arrow => new ArrowAnnotation { Start = a, End = b, Stroke = DefaultStroke, Thickness = DefaultThickness },
        AnnotationTool.Mosaic => new MosaicAnnotation { Rect = RectInt.FromPoints(a, b), BlockSize = 12, Stroke = DefaultStroke, Thickness = 1 },
        _ => new RectangleAnnotation { Rect = RectInt.FromPoints(a, b), Stroke = DefaultStroke, Thickness = DefaultThickness },
    };

    private static bool IsValidAnnotation(AnnotationElement element) => element switch
    {
        FreehandAnnotation f => f.Points.Count >= 2,
        ArrowAnnotation a => a.Start.DistanceTo(a.End) >= 3,
        _ => element.GetBounds() is { Width: >= 3 } or { Height: >= 3 },
    };

    // ---------- 几何（绝对物理）----------

    private static PointInt ToPi(Point p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));

    private Point ClampToSelection(Point p) => new(
        Math.Clamp(p.X, Selection.Left, Selection.Right),
        Math.Clamp(p.Y, Selection.Top, Selection.Bottom));

    private static Rect MakeRect(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static bool SameRect(Rect a, Rect b)
        => Math.Abs(a.X - b.X) < 0.01
           && Math.Abs(a.Y - b.Y) < 0.01
           && Math.Abs(a.Width - b.Width) < 0.01
           && Math.Abs(a.Height - b.Height) < 0.01;

    private Rect ClipToBounds(Rect r)
    {
        double x = Math.Max(_vs.X, r.X), y = Math.Max(_vs.Y, r.Y);
        double right = Math.Min(_vs.Right, r.Right), bottom = Math.Min(_vs.Bottom, r.Bottom);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private Rect ClampMove(Rect r)
    {
        double x = Math.Clamp(r.X, _vs.X, Math.Max(_vs.X, _vs.Right - r.Width));
        double y = Math.Clamp(r.Y, _vs.Y, Math.Max(_vs.Y, _vs.Bottom - r.Height));
        return new Rect(x, y, r.Width, r.Height);
    }

    private Rect ResizeSelection(Rect s, int handle, Point p)
    {
        double l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
        switch (handle)
        {
            case 0: l = p.X; t = p.Y; break;
            case 1: t = p.Y; break;
            case 2: r = p.X; t = p.Y; break;
            case 3: r = p.X; break;
            case 4: r = p.X; b = p.Y; break;
            case 5: b = p.Y; break;
            case 6: l = p.X; b = p.Y; break;
            case 7: l = p.X; break;
        }
        return ClipToBounds(new Rect(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t)));
    }

    public Point[] HandlePoints(Rect s)
    {
        double l = s.Left, t = s.Top, r = s.Right, b = s.Bottom, cx = (l + r) / 2, cy = (t + b) / 2;
        return
        [
            new Point(l, t), new Point(cx, t), new Point(r, t), new Point(r, cy),
            new Point(r, b), new Point(cx, b), new Point(l, b), new Point(l, cy),
        ];
    }

    private int HitTestHandle(Point p)
    {
        var pts = HandlePoints(Selection);
        for (int i = 0; i < 8; i++)
            if (Math.Abs(p.X - pts[i].X) <= HandleSize && Math.Abs(p.Y - pts[i].Y) <= HandleSize)
                return i;
        return -1;
    }

    /// <summary>在覆盖层显示前枚举的窗口列表中，按 Z 序找最上层包含该点的窗口。</summary>
    private WindowEnumerator.WindowBounds? FindWindowAt(Point p)
    {
        foreach (var w in _topLevelWindows)
        {
            if (p.X >= w.X && p.X < w.X + w.Width && p.Y >= w.Y && p.Y < w.Y + w.Height)
                return w;
        }
        return null;
    }

    public Color SamplePixel(int x, int y)
    {
        int bx = x - _vs.X, by = y - _vs.Y;
        if (bx < 0 || by < 0 || bx >= _background.PixelWidth || by >= _background.PixelHeight)
            return Colors.Black;
        _background.CopyPixels(new Int32Rect(bx, by, 1, 1), _samplePixelBuffer, 4, 0);
        return Color.FromRgb(_samplePixelBuffer[2], _samplePixelBuffer[1], _samplePixelBuffer[0]);
    }

    /// <summary>复制当前光标处像素颜色到剪贴板（C = HEX，Shift+C = RGB）。</summary>
    private void CopyColor(bool asRgb)
    {
        var c = SamplePixel((int)Math.Round(_lastCursor.X), (int)Math.Round(_lastCursor.Y));
        string text = asRgb ? $"{c.R}, {c.G}, {c.B}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        try
        {
            System.Windows.Clipboard.SetText(text);
            Log.Information("已复制颜色：{Text}", text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "复制颜色失败");
        }
    }

    // ---------- 协调渲染 ----------

    private void RenderAll()
    {
        foreach (var w in _windows) w.RenderState();
        UpdateToolbar();
    }

    private void UpdateMagnifier(Point cursor)
    {
        bool show = !HasSelection && Mode == DragMode.None && !_drawingAnnotation;
        foreach (var w in _windows)
        {
            if (show && w.Monitor is var m &&
                cursor.X >= m.X && cursor.X < m.Right && cursor.Y >= m.Y && cursor.Y < m.Bottom)
                w.ShowMagnifier(cursor);
            else
                w.HideMagnifier();
        }
    }

    private void UpdateToolbar()
    {
        bool show = HasSelection && Mode == DragMode.None;
        // 工具栏交给选区右下角所在（或相交面积最大）的屏承载
        CaptureScreenWindow? host = null;
        if (show)
        {
            foreach (var w in _windows)
            {
                var m = w.Monitor;
                double br_x = Selection.Right, br_y = Selection.Bottom;
                if (br_x >= m.X && br_x <= m.Right && br_y >= m.Y && br_y <= m.Bottom) { host = w; break; }
            }
            host ??= _windows.FirstOrDefault(w => IntersectsMonitor(w.Monitor, Selection));
        }
        foreach (var w in _windows)
        {
            if (w == host) w.ShowToolbar();
            else w.HideToolbar();
        }
    }

    private static bool IntersectsMonitor(MonitorInfo m, Rect sel)
        => sel.Right > m.X && sel.Left < m.Right && sel.Bottom > m.Y && sel.Top < m.Bottom;

    // ---------- 输出 ----------

    private BitmapSource RenderFinal()
    {
        int x = Math.Clamp((int)Math.Round(Selection.X) - _vs.X, 0, _background.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(Selection.Y) - _vs.Y, 0, _background.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Round(Selection.Width), 1, _background.PixelWidth - x);
        int h = Math.Clamp((int)Math.Round(Selection.Height), 1, _background.PixelHeight - y);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var crop = new CroppedBitmap(_background, new Int32Rect(x, y, w, h));
            dc.DrawImage(crop, new Rect(0, 0, w, h));
            dc.PushTransform(new TranslateTransform(-Selection.X, -Selection.Y));
            foreach (var element in Document.InZOrder())
                AnnotationCanvas.RenderElement(dc, element, _background, _vs.X, _vs.Y);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static CapturedImage ToCaptured(BitmapSource bmp)
    {
        BitmapSource src = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var buf = new byte[stride * h];
        src.CopyPixels(buf, stride, 0);
        return new CapturedImage(w, h, buf);
    }

    public void DoCopy()
    {
        if (!HasSelection) { Cancel(); return; }
        if (_sessionMode == SessionMode.PinCapture)
        {
            DoPin();
            return;
        }
        if (_sessionMode == SessionMode.LongCapture)
        {
            LongCaptureConfirmed?.Invoke(
                (int)Math.Round(Selection.X), (int)Math.Round(Selection.Y),
                (int)Math.Round(Selection.Width), (int)Math.Round(Selection.Height));
            Cancel();
            return;
        }
        if (_sessionMode == SessionMode.GifCapture)
        {
            ConfirmGifCapture(GifOutputMode.Copy);
            return;
        }
        try
        {
            var final = RenderFinal();
            string? tempPath = null;
            try
            {
                tempPath = TempFileCleaner.BuildTempPath("png");
                ImageSaver.Save(final, tempPath);
            }
            catch { tempPath = null; }

            ClipboardHelper.CopyImage(ToCaptured(final), tempPath);
            Log.Information("已复制选区到剪贴板：{W}x{H}，附带文件={F}", final.PixelWidth, final.PixelHeight, tempPath ?? "无");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "复制到剪贴板失败");
        }
        Cancel();
    }

    public void DoSave()
    {
        if (!HasSelection) return;
        if (_sessionMode == SessionMode.GifCapture)
        {
            ConfirmGifCapture(GifOutputMode.Save);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            FileName = ImageSaver.BuildDefaultFileName(_defaultSaveFormat),
            AddExtension = true,
            DefaultExt = "." + _defaultSaveFormat,
            FilterIndex = _defaultSaveFormat == "jpg" ? 2 : 1,
        };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                ImageSaver.Save(RenderFinal(), dialog.FileName, _jpegQuality);
                Log.Information("已保存截图：{Path}", dialog.FileName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存截图失败");
            }
            Cancel(); // 仅在确实保存后结束会话
        }
        // 文件对话框点“取消”：保留截图会话，可继续编辑/标注
    }

    private void ConfirmGifCapture(GifOutputMode outputMode)
    {
        int x = (int)Math.Round(Selection.X);
        int y = (int)Math.Round(Selection.Y);
        int w = (int)Math.Round(Selection.Width);
        int h = (int)Math.Round(Selection.Height);
        var handler = GifCaptureConfirmed;
        Cancel();
        handler?.Invoke(x, y, w, h, outputMode);
    }

    /// <summary>把当前选区（含标注）钉到屏幕。</summary>
    public void DoPin()
    {
        if (!HasSelection) return;
        var final = RenderFinal();
        PinRequested?.Invoke(final, ToCaptured(final),
            (int)Math.Round(Selection.X), (int)Math.Round(Selection.Y));
        Cancel();
    }

    private static string NormalizeSaveFormat(string? format)
        => string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase)
            ? "jpg"
            : "png";

    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        var closedHandler = Closed;
        foreach (var w in _windows.ToArray())
        {
            w.FirstFrameRendered -= OnWindowFirstFrameRendered;
            w.Closed -= OnCaptureWindowClosed;
            try
            {
                w.Close();
            }
            catch (InvalidOperationException)
            {
                // Window may already be closing because the user/system closed it first.
            }
        }
        _windows.Clear();
        _topLevelWindows = Array.Empty<WindowEnumerator.WindowBounds>();
        _hoverWindow = null;
        _downHoverWindow = null;
        Preview = null;
        _freehand = null;
        Document.Clear();
        _undo.Clear();
        closedHandler?.Invoke();
        Closed = null;
        PinRequested = null;
        LongCaptureConfirmed = null;
        GifCaptureConfirmed = null;
        Log.Information("截图会话结束");
    }
}
