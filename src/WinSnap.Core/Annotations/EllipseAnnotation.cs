using WinSnap.Core.Primitives;

namespace WinSnap.Core.Annotations;

/// <summary>椭圆标注（以 <see cref="Rect"/> 为外接矩形，仅描边）。</summary>
public sealed class EllipseAnnotation : AnnotationElement
{
    public RectInt Rect { get; set; }

    public override RectInt GetBounds() => Rect.Normalized();

    public override bool HitTest(PointInt p, double tolerance)
    {
        var r = Rect.Normalized();
        if (r.Width <= 0 || r.Height <= 0) return false;

        double tol = EffectiveTolerance(tolerance);
        double cx = r.X + (r.Width / 2.0);
        double cy = r.Y + (r.Height / 2.0);
        double rx = r.Width / 2.0;
        double ry = r.Height / 2.0;

        // 归一化坐标：在椭圆边界上 value≈1。用 tol 在 x/y 半径上换算成带宽。
        double nx = (p.X - cx) / rx;
        double ny = (p.Y - cy) / ry;
        double value = (nx * nx) + (ny * ny);

        // 容差带：把像素容差近似映射到归一化空间（取较小半径方向更保守）
        double minR = Math.Min(rx, ry);
        if (minR <= double.Epsilon) return true;
        double band = (tol + (Thickness / 2.0)) / minR;
        double lo = 1.0 - band;
        double hi = 1.0 + band;
        return value >= lo * lo && value <= hi * hi;
    }

    public override AnnotationElement Clone()
    {
        var c = new EllipseAnnotation { Rect = Rect };
        CopyBaseTo(c);
        return c;
    }

    protected override void CopyGeometryFrom(AnnotationElement source)
        => Rect = ((EllipseAnnotation)source).Rect;
}
