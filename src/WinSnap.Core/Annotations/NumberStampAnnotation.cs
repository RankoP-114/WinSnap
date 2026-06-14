using WinSnap.Core.Primitives;

namespace WinSnap.Core.Annotations;

/// <summary>
/// 编号标记（带数字的圆形徽标），常用于步骤标注。
/// <see cref="Center"/> 为圆心，<see cref="Radius"/> 为半径，<see cref="Number"/> 为显示数字。
/// </summary>
public sealed class NumberStampAnnotation : AnnotationElement
{
    public PointInt Center { get; set; }
    public int Number { get; set; } = 1;

    /// <summary>圆半径（像素）。</summary>
    public double Radius { get; set; } = 14.0;

    public override RectInt GetBounds()
    {
        int r = (int)Math.Ceiling(Radius);
        return new RectInt(Center.X - r, Center.Y - r, 2 * r, 2 * r);
    }

    public override bool HitTest(PointInt p, double tolerance)
    {
        // 实心圆：圆内（含容差）即命中
        double reach = Radius + Math.Max(tolerance, Thickness / 2.0);
        return p.DistanceTo(Center) <= reach;
    }

    public override AnnotationElement Clone()
    {
        var c = new NumberStampAnnotation { Center = Center, Number = Number, Radius = Radius };
        CopyBaseTo(c);
        return c;
    }

    protected override void CopyGeometryFrom(AnnotationElement source)
    {
        var s = (NumberStampAnnotation)source;
        Center = s.Center;
        Number = s.Number;
        Radius = s.Radius;
    }
}
