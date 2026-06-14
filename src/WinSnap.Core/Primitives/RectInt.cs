namespace WinSnap.Core.Primitives;

/// <summary>
/// 平台无关的整数矩形（左上角 + 宽高，像素）。值语义。
/// 允许负的 <see cref="Width"/> / <see cref="Height"/>（拖拽框选时方向可任意），
/// 需要规范化时调用 <see cref="Normalized"/>。
/// </summary>
public readonly record struct RectInt(int X, int Y, int Width, int Height)
{
    /// <summary>空矩形 (0,0,0,0)。</summary>
    public static RectInt Empty => new(0, 0, 0, 0);

    public int Left => X;
    public int Top => Y;

    /// <summary>右边界（X + Width）。注意负宽时可能小于 Left。</summary>
    public int Right => X + Width;

    /// <summary>下边界（Y + Height）。注意负高时可能小于 Top。</summary>
    public int Bottom => Y + Height;

    /// <summary>是否为零面积（宽或高为 0）。</summary>
    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>左上角点。</summary>
    public PointInt Location => new(X, Y);

    /// <summary>规范化后的几何中心（向下取整）。</summary>
    public PointInt Center
    {
        get
        {
            var n = Normalized();
            return new PointInt(n.X + (n.Width / 2), n.Y + (n.Height / 2));
        }
    }

    /// <summary>用两个角点构造（顺序任意，结果未规范化为正宽高 — 调 Normalized 获取）。</summary>
    public static RectInt FromPoints(PointInt a, PointInt b)
        => new(a.X, a.Y, b.X - a.X, b.Y - a.Y);

    /// <summary>用左/上/右/下边界构造（要求 left&lt;=right、top&lt;=bottom 才得到正宽高）。</summary>
    public static RectInt FromLTRB(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>
    /// 返回宽高均非负的等价矩形：负宽/负高时把原点移到真正的左上角并取绝对值。
    /// </summary>
    public RectInt Normalized()
    {
        int x = Width >= 0 ? X : X + Width;
        int y = Height >= 0 ? Y : Y + Height;
        int w = Width >= 0 ? Width : -Width;
        int h = Height >= 0 ? Height : -Height;
        return new RectInt(x, y, w, h);
    }

    /// <summary>点是否落在矩形内（含左/上边，不含右/下边）。会先规范化。</summary>
    public bool Contains(PointInt p)
    {
        var n = Normalized();
        return p.X >= n.X && p.X < n.X + n.Width
            && p.Y >= n.Y && p.Y < n.Y + n.Height;
    }

    /// <summary>另一矩形是否被完全包含。会先规范化。</summary>
    public bool Contains(RectInt other)
    {
        var n = Normalized();
        var o = other.Normalized();
        return o.X >= n.X && o.Y >= n.Y
            && o.X + o.Width <= n.X + n.Width
            && o.Y + o.Height <= n.Y + n.Height;
    }

    /// <summary>与另一矩形是否相交（规范化后判断，边接触不算相交）。</summary>
    public bool IntersectsWith(RectInt other)
    {
        var n = Normalized();
        var o = other.Normalized();
        return o.X < n.X + n.Width && o.X + o.Width > n.X
            && o.Y < n.Y + n.Height && o.Y + o.Height > n.Y;
    }

    /// <summary>两矩形的交集；不相交返回 <see cref="Empty"/>。</summary>
    public RectInt Intersect(RectInt other)
    {
        var n = Normalized();
        var o = other.Normalized();
        int left = Math.Max(n.X, o.X);
        int top = Math.Max(n.Y, o.Y);
        int right = Math.Min(n.X + n.Width, o.X + o.Width);
        int bottom = Math.Min(n.Y + n.Height, o.Y + o.Height);
        if (right <= left || bottom <= top)
            return Empty;
        return new RectInt(left, top, right - left, bottom - top);
    }

    /// <summary>包含两矩形的最小外接矩形（并集包围盒）。会先规范化。</summary>
    public RectInt Union(RectInt other)
    {
        var n = Normalized();
        var o = other.Normalized();
        // 若一方为空，直接返回另一方
        if (n.IsEmpty) return o;
        if (o.IsEmpty) return n;
        int left = Math.Min(n.X, o.X);
        int top = Math.Min(n.Y, o.Y);
        int right = Math.Max(n.X + n.Width, o.X + o.Width);
        int bottom = Math.Max(n.Y + n.Height, o.Y + o.Height);
        return new RectInt(left, top, right - left, bottom - top);
    }

    /// <summary>四周外扩 <paramref name="margin"/> 像素（负值内缩）。会先规范化。</summary>
    public RectInt Inflate(int margin) => Inflate(margin, margin);

    /// <summary>水平/垂直方向分别外扩。会先规范化。</summary>
    public RectInt Inflate(int dx, int dy)
    {
        var n = Normalized();
        return new RectInt(n.X - dx, n.Y - dy, n.Width + (2 * dx), n.Height + (2 * dy));
    }

    /// <summary>整体平移。</summary>
    public RectInt Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public override string ToString() => $"[{X}, {Y}, {Width}x{Height}]";
}
