using WinSnap.Core.Primitives;

namespace WinSnap.Core.Annotations;

/// <summary>箭头标注：从 <see cref="Start"/> 指向 <see cref="End"/>，箭头大小 <see cref="HeadSize"/>。</summary>
public sealed class ArrowAnnotation : AnnotationElement
{
    public PointInt Start { get; set; }
    public PointInt End { get; set; }

    /// <summary>箭头头部尺寸（像素，逻辑值）。</summary>
    public double HeadSize { get; set; } = 12.0;

    public override RectInt GetBounds()
    {
        int left = Math.Min(Start.X, End.X);
        int top = Math.Min(Start.Y, End.Y);
        int right = Math.Max(Start.X, End.X);
        int bottom = Math.Max(Start.Y, End.Y);
        // 计入箭头头部可能的外扩
        int pad = (int)Math.Ceiling(HeadSize);
        return RectInt.FromLTRB(left - pad, top - pad, right + pad, bottom + pad);
    }

    public override bool HitTest(PointInt p, double tolerance)
        => DistancePointToSegment(p, Start, End) <= EffectiveTolerance(tolerance) + 1.0;

    public override AnnotationElement Clone()
    {
        var c = new ArrowAnnotation { Start = Start, End = End, HeadSize = HeadSize };
        CopyBaseTo(c);
        return c;
    }

    protected override void CopyGeometryFrom(AnnotationElement source)
    {
        var s = (ArrowAnnotation)source;
        Start = s.Start;
        End = s.End;
        HeadSize = s.HeadSize;
    }
}
