using WinSnap.Core.Primitives;

namespace WinSnap.Core.Annotations;

/// <summary>
/// 所有标注图元的抽象基类。平台无关：只描述几何与样式，渲染交由 App 层。
/// </summary>
public abstract class AnnotationElement
{
    /// <summary>稳定唯一标识。Clone 会保留同一 Id（表示"同一逻辑元素的不同状态/快照"）。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>叠放层级，值越大越靠上（越晚绘制）。</summary>
    public int ZIndex { get; set; }

    /// <summary>描边颜色。</summary>
    public ColorRgba Stroke { get; set; } = ColorRgba.Red;

    /// <summary>描边粗细（像素，逻辑值）。</summary>
    public double Thickness { get; set; } = 2.0;

    /// <summary>返回包围本图元的轴对齐包围盒（已规范化为正宽高）。</summary>
    public abstract RectInt GetBounds();

    /// <summary>
    /// 命中测试：点 <paramref name="p"/> 是否落在图元（含 <paramref name="tolerance"/> 容差）上。
    /// 容差用于细线/路径的"靠近即命中"。
    /// </summary>
    public abstract bool HitTest(PointInt p, double tolerance);

    /// <summary>深拷贝（含 Id、ZIndex、样式与几何）。用于撤销/重做快照。</summary>
    public abstract AnnotationElement Clone();

    /// <summary>
    /// 把 <paramref name="source"/> 的状态（基类样式 + 子类几何）原地拷贝到本实例，
    /// 不改变本实例的引用标识。要求 <paramref name="source"/> 与本实例同为一种具体类型。
    /// 供 <c>TransformElementCommand</c> 把"变换前/后快照"应用回文档中的 live 元素。
    /// </summary>
    public void CopyStateFrom(AnnotationElement source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.GetType() != GetType())
            throw new ArgumentException(
                $"类型不匹配：期望 {GetType().Name}，实际 {source.GetType().Name}。", nameof(source));
        Id = source.Id;
        ZIndex = source.ZIndex;
        Stroke = source.Stroke;
        Thickness = source.Thickness;
        CopyGeometryFrom(source);
    }

    /// <summary>子类把 <paramref name="source"/> 的几何字段拷到 this（调用方已保证同类型）。</summary>
    protected abstract void CopyGeometryFrom(AnnotationElement source);

    /// <summary>把基类公共字段拷贝到 <paramref name="target"/>。子类 Clone 实现复用。</summary>
    protected void CopyBaseTo(AnnotationElement target)
    {
        target.Id = Id;
        target.ZIndex = ZIndex;
        target.Stroke = Stroke;
        target.Thickness = Thickness;
    }

    // ---- 几何命中辅助（供子类复用）----

    /// <summary>点到线段 AB 的最短距离。</summary>
    protected static double DistancePointToSegment(PointInt p, PointInt a, PointInt b)
    {
        double abx = b.X - a.X;
        double aby = b.Y - a.Y;
        double apx = p.X - a.X;
        double apy = p.Y - a.Y;
        double lenSq = (abx * abx) + (aby * aby);
        if (lenSq <= double.Epsilon)
        {
            // A、B 重合，退化为点距
            return Math.Sqrt((apx * apx) + (apy * apy));
        }
        double t = ((apx * abx) + (apy * aby)) / lenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        double cx = a.X + (t * abx);
        double cy = a.Y + (t * aby);
        double dx = p.X - cx;
        double dy = p.Y - cy;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>命中容差与描边一半的较大者（粗线更易命中）。</summary>
    protected double EffectiveTolerance(double tolerance)
        => Math.Max(tolerance, Thickness / 2.0);
}
