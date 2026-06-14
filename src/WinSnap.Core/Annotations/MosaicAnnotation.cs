using WinSnap.Core.Primitives;

namespace WinSnap.Core.Annotations;

/// <summary>马赛克 / 模糊处理方式。</summary>
public enum MosaicMode
{
    /// <summary>像素化（按 BlockSize 取块平均色）。</summary>
    Pixelate,

    /// <summary>高斯/盒式模糊（BlockSize 近似为模糊半径）。</summary>
    Blur,
}

/// <summary>
/// 马赛克标注：在 <see cref="Rect"/> 区域内做像素化或模糊。
/// 实际像素处理在 App 层基于底图执行；本类只携带区域与参数。
/// </summary>
public sealed class MosaicAnnotation : AnnotationElement
{
    public RectInt Rect { get; set; }

    /// <summary>块大小（像素化时为块边长；模糊时近似为半径）。</summary>
    public int BlockSize { get; set; } = 12;

    public MosaicMode Mode { get; set; } = MosaicMode.Pixelate;

    public override RectInt GetBounds() => Rect.Normalized();

    public override bool HitTest(PointInt p, double tolerance)
    {
        int tol = (int)Math.Ceiling(Math.Max(0.0, tolerance));
        return Rect.Normalized().Inflate(tol).Contains(p);
    }

    public override AnnotationElement Clone()
    {
        var c = new MosaicAnnotation { Rect = Rect, BlockSize = BlockSize, Mode = Mode };
        CopyBaseTo(c);
        return c;
    }

    protected override void CopyGeometryFrom(AnnotationElement source)
    {
        var s = (MosaicAnnotation)source;
        Rect = s.Rect;
        BlockSize = s.BlockSize;
        Mode = s.Mode;
    }
}
