using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSnap.App.Capture;

/// <summary>
/// 跟随光标的放大镜：放大背景图中光标周围的像素（最近邻），
/// 并显示当前像素的坐标与颜色（RGB/HEX）。
/// </summary>
public partial class MagnifierControl : UserControl
{
    private const int ZoomSourceSize = 15; // 源区域边长（像素），放大到 120 约 8x

    public MagnifierControl()
    {
        InitializeComponent();
    }

    /// <summary>用背景图与光标物理像素坐标刷新放大镜内容。</summary>
    public void Update(BitmapSource source, int px, int py, Color color)
    {
        const int half = ZoomSourceSize / 2;
        int maxX = Math.Max(0, source.PixelWidth - ZoomSourceSize);
        int maxY = Math.Max(0, source.PixelHeight - ZoomSourceSize);
        int sx = Math.Clamp(px - half, 0, maxX);
        int sy = Math.Clamp(py - half, 0, maxY);

        var crop = new CroppedBitmap(source,
            new Int32Rect(sx, sy, ZoomSourceSize, ZoomSourceSize));
        if (crop.CanFreeze)
            crop.Freeze();
        ZoomImage.Source = crop;

        PosText.Text = $"({px}, {py})";
        ColorText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}  {color.R},{color.G},{color.B}";
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze)
            brush.Freeze();
        ColorSample.Fill = brush;
    }

    public void Clear()
    {
        ZoomImage.Source = null;
        PosText.Text = string.Empty;
        ColorText.Text = string.Empty;
        ColorSample.Fill = null;
    }
}
