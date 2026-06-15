using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using WinSnap.App.Diagnostics;
using WinSnap.App.Imaging;
using WinSnap.Interop;

namespace WinSnap.App.Pin;

/// <summary>
/// 「钉图」窗口：把一张截图固定为屏幕上独立的无边框置顶小窗口。
/// <para>
/// 渲染原则：图像本身是物理像素的 <see cref="BitmapSource"/>（96 DPI、PixelWidth=物理宽）。
/// 为在任意 DPI 显示器上 100% 时 1:1 精确显示，<see cref="OnSourceInitialized"/> 里用
/// <see cref="ScreenWindowHelper.PositionTopmost"/> 按物理像素定位窗口；图像的逻辑(DIU)尺寸取
/// 物理像素 × 用户缩放 ÷ 本屏 DPI，使一个图像像素恰好落在一个屏幕像素上。用户缩放（滚轮）叠加在
/// 该基准之上，窗口物理尺寸随之重新定位（保持鼠标处为锚点）。
/// </para>
/// 交互：左键拖动移动；滚轮缩放(10%~800%，以鼠标为中心)；Ctrl+滚轮调透明度(0.2~1.0)；
/// 右键菜单（复制/另存为/100%/边框显隐/关闭）；双击或 Esc 关闭。
/// </summary>
public partial class PinWindow : Window
{
    private const double MinScale = 0.10;
    private const double MaxScale = 8.00;
    private const double MinOpacity = 0.20;
    private const double MaxOpacity = 1.00;

    private readonly BitmapSource _image;
    private readonly int _pixelWidth;   // 图像物理像素宽
    private readonly int _pixelHeight;  // 图像物理像素高

    private CapturedImage? _captured;   // 复制到剪贴板用；缺省时按需从 _image 转出
    private double _dpiScale = 1.0;     // 本窗口所在显示器的 DPI 缩放（device/DIU）
    private double _userScale = 1.0;    // 用户缩放因子（滚轮）
    private int _physicalX;             // 当前窗口左上角物理像素 X
    private int _physicalY;             // 当前窗口左上角物理像素 Y

    /// <summary>窗口关闭时触发，供 <see cref="PinManager"/> 从实例列表移除。</summary>
    public event EventHandler? PinClosed;

    /// <summary>
    /// 创建一个钉图窗口。
    /// </summary>
    /// <param name="image">要显示的位图（物理像素，建议已 Freeze）。</param>
    /// <param name="captured">可选的原始捕获数据，用于复制到剪贴板；为 null 时复制操作会从 <paramref name="image"/> 即时转换。</param>
    /// <param name="physicalX">初始左上角的屏幕物理像素 X。</param>
    /// <param name="physicalY">初始左上角的屏幕物理像素 Y。</param>
    public PinWindow(BitmapSource image, CapturedImage? captured, int physicalX, int physicalY)
    {
        ArgumentNullException.ThrowIfNull(image);
        InitializeComponent();

        _image = image;
        _captured = captured;
        _pixelWidth = image.PixelWidth;
        _pixelHeight = image.PixelHeight;
        _physicalX = physicalX;
        _physicalY = physicalY;

        PinImage.Source = _image;

        var menu = BuildContextMenu();
        ContextMenu = menu;
        FrameBorder.ContextMenu = menu;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 本窗口所在显示器的 DPI 缩放（PerMonitorV2 下随显示器变化）
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiScale = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;

        UpdateImageSize(); // 设定图像 DIU 尺寸（含 _userScale 与 DPI 换算）
        ApplyLayout();     // 按当前缩放把窗口定位到精确物理矩形
        Opacity = 1.0;     // 布局就绪后再显形，避免初始跳变
    }

    // ---------------------------------------------------------------------
    // 缩放 / 透明度 / 布局
    // ---------------------------------------------------------------------

    /// <summary>边框单边的物理像素数（BorderThickness 为 1 DIU = _dpiScale 物理像素；隐藏时为 0）。</summary>
    private int BorderPhysical()
        => FrameBorder.BorderThickness.Left > 0 ? Math.Max(1, (int)Math.Round(_dpiScale)) : 0;

    /// <summary>当前窗口的物理宽高（图像物理尺寸 × 用户缩放，含边框）。</summary>
    private (int Width, int Height) CurrentPhysicalSize()
    {
        int b = BorderPhysical();
        int w = Math.Max(1, (int)Math.Round(_pixelWidth * _userScale)) + b * 2;
        int h = Math.Max(1, (int)Math.Round(_pixelHeight * _userScale)) + b * 2;
        return (w, h);
    }

    /// <summary>
    /// 按当前 <see cref="_userScale"/> 与 DPI 设定图像的 DIU 尺寸：
    /// 物理渲染宽 = PixelWidth × _userScale（DIU = 物理 / _dpiScale），使 100% 时 1 图像像素 = 1 屏幕像素。
    /// </summary>
    private void UpdateImageSize()
    {
        PinImage.Width = _pixelWidth * _userScale / _dpiScale;
        PinImage.Height = _pixelHeight * _userScale / _dpiScale;
    }

    /// <summary>按当前物理左上角与缩放，把窗口置顶定位到精确物理矩形。</summary>
    private void ApplyLayout()
    {
        if (!IsLoaded && PresentationSource.FromVisual(this) is null)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var (w, h) = CurrentPhysicalSize();
        ScreenWindowHelper.PositionTopmost(hwnd, _physicalX, _physicalY, w, h);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            // Ctrl+滚轮：调透明度
            double step = e.Delta > 0 ? 0.05 : -0.05;
            Opacity = Math.Clamp(Opacity + step, MinOpacity, MaxOpacity);
            return;
        }

        // 滚轮：以鼠标位置为锚点缩放
        double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        ZoomAt(e.GetPosition(this), factor);
    }

    /// <summary>以窗口内某点为不动点缩放图片，并重定位窗口物理矩形保持该点贴合鼠标。</summary>
    private void ZoomAt(Point anchorInWindow, double factor)
    {
        double oldScale = _userScale;
        double newScale = Math.Clamp(oldScale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - oldScale) < 1e-6)
            return;

        // 锚点在内容内的归一化位置（0..1），用 DIU 尺寸与缩放反推
        var (oldW, oldH) = CurrentPhysicalSize();
        double oldPhysAnchorX = anchorInWindow.X * _dpiScale; // DIU → 物理
        double oldPhysAnchorY = anchorInWindow.Y * _dpiScale;
        double fx = oldW > 0 ? oldPhysAnchorX / oldW : 0.5;
        double fy = oldH > 0 ? oldPhysAnchorY / oldH : 0.5;

        _userScale = newScale;
        UpdateImageSize();
        var (newW, newH) = CurrentPhysicalSize();

        // 让锚点的物理屏幕位置保持不变：调整窗口左上角
        _physicalX += (int)Math.Round(oldPhysAnchorX - fx * newW);
        _physicalY += (int)Math.Round(oldPhysAnchorY - fy * newH);

        ApplyLayout();
    }

    private void SetScale(double scale)
    {
        // 以窗口中心为锚点设定绝对缩放（用于「100% 原始大小」）
        double factor = scale / _userScale;
        var (w, h) = CurrentPhysicalSize();
        ZoomAt(new Point((w / _dpiScale) / 2.0, (h / _dpiScale) / 2.0), factor);
    }

    // ---------------------------------------------------------------------
    // 拖动 / 双击 / 键盘
    // ---------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        // 左键拖动移动窗口；拖动结束后同步物理坐标，保持后续缩放锚点正确
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove 仅在主鼠标按下时有效；偶发竞态忽略
            }
            SyncPhysicalFromWindow();
        }
    }

    /// <summary>拖动后从窗口当前逻辑位置回算物理左上角（DragMove 改的是 Left/Top，DIU）。</summary>
    private void SyncPhysicalFromWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        if (NativeGetWindowRect(hwnd, out var r))
        {
            _physicalX = r.Left;
            _physicalY = r.Top;
            // 重新置顶定位，确保 Topmost 与精确物理尺寸（DragMove 可能引入舍入）一致
            ApplyLayout();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        PinClosed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------
    // 右键菜单与操作
    // ---------------------------------------------------------------------

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MakeItem("复制", (_, _) => CopyToClipboard()));
        menu.Items.Add(MakeItem("另存为…", (_, _) => SaveAs()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("100% 原始大小", (_, _) => SetScale(1.0)));

        var toggleBorder = new MenuItem { Header = "隐藏边框" };
        toggleBorder.Click += (_, _) => ToggleBorder(toggleBorder);
        menu.Items.Add(toggleBorder);

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("关闭", (_, _) => Close()));
        return menu;
    }

    private static MenuItem MakeItem(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private void ToggleBorder(MenuItem source)
    {
        bool visible = FrameBorder.BorderThickness.Left > 0;
        FrameBorder.BorderThickness = visible ? new Thickness(0) : new Thickness(1);
        source.Header = visible ? "显示边框" : "隐藏边框";
        ApplyLayout(); // 边框增减改变窗口物理尺寸
    }

    private void CopyToClipboard()
    {
        try
        {
            var captured = _captured ??= ToCapturedImage(_image);
            string? tempPng = TempFileCleaner.BuildTempPath("png");
            try
            {
                File.WriteAllBytes(tempPng, ClipboardHelper.EncodePng(captured));
            }
            catch
            {
                tempPng = null; // PNG 落盘失败不阻断复制，仅缺 CF_HDROP（粘贴成文件）
            }
            ClipboardHelper.CopyImage(captured, tempPng);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "钉图：复制到剪贴板失败");
        }
    }

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存钉图",
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg)|*.jpg",
            FileName = ImageSaver.BuildDefaultFileName("png"),
            DefaultExt = ".png",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ImageSaver.Save(_image, dialog.FileName); // 复用：按扩展名选 PNG / JPEG 编码器
        }
        catch (Exception ex)
        {
            Log.Error(ex, "钉图：另存为失败");
            MessageBox.Show(this, $"保存失败：{ex.Message}", "WinSnap",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>把 WPF 位图转为 <see cref="CapturedImage"/>（Bgra32，top-down，Stride=Width*4）。</summary>
    private static CapturedImage ToCapturedImage(BitmapSource src)
    {
        BitmapSource bgra = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        int w = bgra.PixelWidth;
        int h = bgra.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        return new CapturedImage(w, h, pixels);
    }

    // ---------------------------------------------------------------------
    // P/Invoke：读取窗口物理矩形（拖动后回算物理坐标）
    // ---------------------------------------------------------------------

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private static bool NativeGetWindowRect(IntPtr hwnd, out RECT rect) => GetWindowRect(hwnd, out rect);
}
