namespace WinSnap.Core.Primitives;

/// <summary>
/// 平台无关的整数二维坐标点（像素）。值语义，不依赖任何 UI / Win32 类型。
/// </summary>
public readonly record struct PointInt(int X, int Y)
{
    /// <summary>原点 (0,0)。</summary>
    public static PointInt Zero => new(0, 0);

    /// <summary>分量相加。</summary>
    public PointInt Offset(int dx, int dy) => new(X + dx, Y + dy);

    /// <summary>按向量偏移。</summary>
    public PointInt Offset(PointInt delta) => new(X + delta.X, Y + delta.Y);

    public static PointInt operator +(PointInt a, PointInt b) => new(a.X + b.X, a.Y + b.Y);

    public static PointInt operator -(PointInt a, PointInt b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>到另一点的欧氏距离。</summary>
    public double DistanceTo(PointInt other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public override string ToString() => $"({X}, {Y})";
}
