using WinSnap.Core.Primitives;

namespace WinSnap.Core.Tests;

public class PrimitivesTests
{
    [Fact]
    public void RectInt_RightBottom_Computed()
    {
        var r = new RectInt(10, 20, 30, 40);
        Assert.Equal(40, r.Right);
        Assert.Equal(60, r.Bottom);
        Assert.Equal(new PointInt(10, 20), r.Location);
    }

    [Fact]
    public void RectInt_Normalized_HandlesNegativeSize()
    {
        var r = new RectInt(50, 60, -20, -30);
        var n = r.Normalized();
        Assert.Equal(new RectInt(30, 30, 20, 30), n);
        Assert.Equal(30, n.X);
        Assert.Equal(30, n.Y);
        Assert.Equal(20, n.Width);
        Assert.Equal(30, n.Height);
    }

    [Theory]
    [InlineData(10, 20, true)]   // 左上角（含）
    [InlineData(39, 59, true)]   // 右下内侧
    [InlineData(40, 60, false)]  // 右下边界（不含）
    [InlineData(5, 25, false)]   // 左外
    public void RectInt_Contains_Point(int px, int py, bool expected)
    {
        var r = new RectInt(10, 20, 30, 40);
        Assert.Equal(expected, r.Contains(new PointInt(px, py)));
    }

    [Fact]
    public void RectInt_Contains_WorksWithNegativeSize()
    {
        // 负宽高也应正确包含
        var r = new RectInt(40, 60, -30, -40); // 等价 (10,20,30,40)
        Assert.True(r.Contains(new PointInt(15, 25)));
        Assert.False(r.Contains(new PointInt(45, 65)));
    }

    [Fact]
    public void RectInt_FromPoints_And_LTRB()
    {
        var r = RectInt.FromPoints(new PointInt(5, 5), new PointInt(15, 25));
        Assert.Equal(new RectInt(5, 5, 10, 20), r);

        var r2 = RectInt.FromLTRB(5, 5, 15, 25);
        Assert.Equal(new RectInt(5, 5, 10, 20), r2);
    }

    [Fact]
    public void RectInt_Intersect_And_Union()
    {
        var a = new RectInt(0, 0, 10, 10);
        var b = new RectInt(5, 5, 10, 10);
        Assert.True(a.IntersectsWith(b));
        Assert.Equal(new RectInt(5, 5, 5, 5), a.Intersect(b));
        Assert.Equal(new RectInt(0, 0, 15, 15), a.Union(b));

        var c = new RectInt(100, 100, 5, 5);
        Assert.False(a.IntersectsWith(c));
        Assert.Equal(RectInt.Empty, a.Intersect(c));
    }

    [Fact]
    public void RectInt_Inflate()
    {
        var r = new RectInt(10, 10, 20, 20);
        Assert.Equal(new RectInt(8, 8, 24, 24), r.Inflate(2));
        Assert.Equal(new RectInt(12, 11, 16, 18), r.Inflate(-2, -1));
    }

    [Fact]
    public void PointInt_Distance_And_Ops()
    {
        var a = new PointInt(0, 0);
        var b = new PointInt(3, 4);
        Assert.Equal(5.0, a.DistanceTo(b), 6);
        Assert.Equal(new PointInt(3, 4), a + b);
        Assert.Equal(new PointInt(-3, -4), a - b);
        Assert.Equal(new PointInt(1, 1), a.Offset(1, 1));
    }

    [Fact]
    public void ColorRgba_ArgbRoundTrip()
    {
        var c = ColorRgba.FromArgb(0x80123456);
        Assert.Equal(0x80, c.A);
        Assert.Equal(0x12, c.R);
        Assert.Equal(0x34, c.G);
        Assert.Equal(0x56, c.B);
        Assert.Equal(0x80123456u, c.ToArgb());
        Assert.Equal((byte)0xFF, c.WithAlpha(0xFF).A);
    }

    [Fact]
    public void PixelBuffer_GetSetPixel_RoundTrip()
    {
        var buf = new PixelBuffer(4, 3);
        Assert.Equal(16, buf.Stride);
        Assert.Equal(4 * 3 * 4, buf.Bgra.Length);

        var color = new ColorRgba(10, 20, 30, 40);
        buf.SetPixel(2, 1, color);
        Assert.Equal(color, buf.GetPixel(2, 1));

        // 验证字节布局确实是 BGRA
        int off = buf.PixelOffset(2, 1);
        Assert.Equal(30, buf.Bgra[off]);     // B
        Assert.Equal(20, buf.Bgra[off + 1]); // G
        Assert.Equal(10, buf.Bgra[off + 2]); // R
        Assert.Equal(40, buf.Bgra[off + 3]); // A
    }

    [Fact]
    public void PixelBuffer_WrapWrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PixelBuffer(2, 2, new byte[10]));
    }

    [Fact]
    public void PixelBuffer_BlitRows_CopiesBlock()
    {
        var src = new PixelBuffer(2, 4);
        for (int y = 0; y < 4; y++)
            src.SetPixel(0, y, new ColorRgba((byte)y, 0, 0, 255));

        var dst = new PixelBuffer(2, 4);
        dst.BlitRows(src, sourceStartRow: 1, destStartRow: 0, rowCount: 2);
        Assert.Equal((byte)1, dst.GetPixel(0, 0).R);
        Assert.Equal((byte)2, dst.GetPixel(0, 1).R);
    }

    [Fact]
    public void PixelBuffer_Clone_IsDeep()
    {
        var a = new PixelBuffer(2, 2);
        a.SetPixel(0, 0, ColorRgba.White);
        var b = a.Clone();
        b.SetPixel(0, 0, ColorRgba.Black);
        Assert.Equal(ColorRgba.White, a.GetPixel(0, 0));
        Assert.Equal(ColorRgba.Black, b.GetPixel(0, 0));
    }
}
