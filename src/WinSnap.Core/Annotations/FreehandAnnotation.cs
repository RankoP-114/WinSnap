using WinSnap.Core.Primitives;

namespace WinSnap.Core.Annotations;

/// <summary>自由画笔标注：折线点序列。</summary>
public sealed class FreehandAnnotation : AnnotationElement
{
    public List<PointInt> Points { get; set; } = new();

    public override RectInt GetBounds()
    {
        if (Points.Count == 0) return RectInt.Empty;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var pt in Points)
        {
            if (pt.X < left) left = pt.X;
            if (pt.Y < top) top = pt.Y;
            if (pt.X > right) right = pt.X;
            if (pt.Y > bottom) bottom = pt.Y;
        }
        return RectInt.FromLTRB(left, top, right, bottom);
    }

    public override bool HitTest(PointInt p, double tolerance)
    {
        double tol = EffectiveTolerance(tolerance) + 1.0;
        if (Points.Count == 0) return false;
        if (Points.Count == 1) return p.DistanceTo(Points[0]) <= tol;
        for (int i = 0; i < Points.Count - 1; i++)
        {
            if (DistancePointToSegment(p, Points[i], Points[i + 1]) <= tol)
                return true;
        }
        return false;
    }

    public override AnnotationElement Clone()
    {
        var c = new FreehandAnnotation { Points = new List<PointInt>(Points) };
        CopyBaseTo(c);
        return c;
    }

    protected override void CopyGeometryFrom(AnnotationElement source)
        => Points = new List<PointInt>(((FreehandAnnotation)source).Points);
}
