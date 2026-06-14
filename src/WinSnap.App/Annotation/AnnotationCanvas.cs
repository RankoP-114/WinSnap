using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinSnap.Core.Annotations;

namespace WinSnap.App.Annotation;

/// <summary>
/// 标注渲染画布：自绘 <see cref="AnnotationDocument"/> 内全部图元（物理像素坐标）+ 预览图元。
/// 马赛克（<see cref="MosaicAnnotation"/>）需要从原始背景实时取样像素化，故由本画布处理；
/// 其余图元交给 <see cref="AnnotationRenderer"/>。
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private AnnotationElement? _preview;

    /// <summary>渲染所用的标注文档（可注入会话共享文档）。</summary>
    public AnnotationDocument Document { get; set; } = new();

    /// <summary>用于马赛克取样的整屏背景（绝对物理像素，原点对应 (MosaicOriginX, MosaicOriginY)）。</summary>
    public BitmapSource? MosaicSource { get; set; }
    public int MosaicOriginX { get; set; }
    public int MosaicOriginY { get; set; }

    public void SetPreview(AnnotationElement? element) => _preview = element;

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        foreach (var element in Document.InZOrder())
            RenderElement(dc, element, MosaicSource, MosaicOriginX, MosaicOriginY);
        if (_preview is not null)
            RenderElement(dc, _preview, MosaicSource, MosaicOriginX, MosaicOriginY);
    }

    public static void RenderElement(
        DrawingContext dc,
        AnnotationElement element,
        BitmapSource? mosaicSource,
        int mosaicOriginX,
        int mosaicOriginY)
    {
        if (element is MosaicAnnotation m && mosaicSource is not null)
            RenderMosaic(dc, m, mosaicSource, mosaicOriginX, mosaicOriginY);
        else
            AnnotationRenderer.Render(dc, element);
    }

    private static void RenderMosaic(
        DrawingContext dc,
        MosaicAnnotation mosaic,
        BitmapSource mosaicSource,
        int mosaicOriginX,
        int mosaicOriginY)
    {
        var r = mosaic.Rect.Normalized();
        if (r.Width < 1 || r.Height < 1)
            return;

        int sx = r.X - mosaicOriginX, sy = r.Y - mosaicOriginY;
        int w = r.Width, h = r.Height;
        // 夹紧到背景范围，超出部分忽略
        if (sx < 0) { w += sx; sx = 0; }
        if (sy < 0) { h += sy; sy = 0; }
        if (sx + w > mosaicSource.PixelWidth) w = mosaicSource.PixelWidth - sx;
        if (sy + h > mosaicSource.PixelHeight) h = mosaicSource.PixelHeight - sy;
        if (w < 1 || h < 1)
            return;

        var pixelated = Pixelate(mosaicSource, sx, sy, w, h, Math.Max(4, mosaic.BlockSize));
        dc.DrawImage(pixelated, new Rect(mosaicOriginX + sx, mosaicOriginY + sy, w, h));
    }

    /// <summary>对背景某区域做块平均像素化，返回 Bgra32 位图。</summary>
    private static BitmapSource Pixelate(BitmapSource src, int sx, int sy, int w, int h, int block)
    {
        var crop = new CroppedBitmap(src, new Int32Rect(sx, sy, w, h));
        int stride = w * 4;
        var px = new byte[stride * h];
        crop.CopyPixels(px, stride, 0);

        for (int by = 0; by < h; by += block)
        for (int bx = 0; bx < w; bx += block)
        {
            int ye = Math.Min(by + block, h), xe = Math.Min(bx + block, w);
            long b = 0, g = 0, r = 0;
            int count = 0;
            for (int y = by; y < ye; y++)
                for (int x = bx; x < xe; x++)
                {
                    int i = y * stride + x * 4;
                    b += px[i]; g += px[i + 1]; r += px[i + 2];
                    count++;
                }
            byte bb = (byte)(b / count), gg = (byte)(g / count), rr = (byte)(r / count);
            for (int y = by; y < ye; y++)
                for (int x = bx; x < xe; x++)
                {
                    int i = y * stride + x * 4;
                    px[i] = bb; px[i + 1] = gg; px[i + 2] = rr; px[i + 3] = 255;
                }
        }

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }
}
