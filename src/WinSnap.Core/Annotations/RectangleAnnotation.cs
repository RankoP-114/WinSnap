using WinSnap.Core.Primitives;

namespace WinSnap.Core.Annotations;

/// <summary>矩形标注（仅描边，内部不填充）。</summary>
public sealed class RectangleAnnotation : AnnotationElement
{
    public RectInt Rect { get; set; }

    public override RectInt GetBounds() => Rect.Normalized();

    public override bool HitTest(PointInt p, double tolerance)
    {
        double tol = EffectiveTolerance(tolerance);
        var r = Rect.Normalized();
        // 命中描边：点在外扩框内、且不在内缩框内（即落在边框带上）
        var outer = r.Inflate((int)Math.Ceiling(tol));
        if (!outer.Contains(p)) return false;
        int inset = (int)Math.Floor(tol);
        // 内缩可能反转，反转时整块都算命中
        if (r.Width <= 2 * inset || r.Height <= 2 * inset) return true;
        var inner = r.Inflate(-inset);
        return !inner.Contains(p);
    }

    public override AnnotationElement Clone()
    {
        var c = new RectangleAnnotation { Rect = Rect };
        CopyBaseTo(c);
        return c;
    }

    protected override void CopyGeometryFrom(AnnotationElement source)
        => Rect = ((RectangleAnnotation)source).Rect;
}
