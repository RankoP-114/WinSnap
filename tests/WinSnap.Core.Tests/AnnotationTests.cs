using WinSnap.Core.Annotations;
using WinSnap.Core.Primitives;

namespace WinSnap.Core.Tests;

public class AnnotationTests
{
    [Fact]
    public void Rectangle_Bounds_Normalized()
    {
        var rect = new RectangleAnnotation { Rect = new RectInt(30, 30, -20, -10) };
        Assert.Equal(new RectInt(10, 20, 20, 10), rect.GetBounds());
    }

    [Fact]
    public void Rectangle_HitTest_OnBorder_NotInside()
    {
        var rect = new RectangleAnnotation { Rect = new RectInt(0, 0, 100, 100), Thickness = 2 };
        Assert.True(rect.HitTest(new PointInt(0, 50), tolerance: 1));   // 左边框上
        Assert.True(rect.HitTest(new PointInt(100, 50), tolerance: 1)); // 右边框上
        Assert.False(rect.HitTest(new PointInt(50, 50), tolerance: 1)); // 正中心（无填充）
    }

    [Fact]
    public void Ellipse_HitTest_OnRing()
    {
        var e = new EllipseAnnotation { Rect = new RectInt(0, 0, 100, 50), Thickness = 2 };
        // 椭圆右顶点 (100,25) 在边界上
        Assert.True(e.HitTest(new PointInt(100, 25), tolerance: 1));
        // 中心不在描边上
        Assert.False(e.HitTest(new PointInt(50, 25), tolerance: 1));
        // 远处不命中
        Assert.False(e.HitTest(new PointInt(200, 25), tolerance: 1));
    }

    [Fact]
    public void Arrow_HitTest_AlongLine()
    {
        var a = new ArrowAnnotation { Start = new PointInt(0, 0), End = new PointInt(100, 0), Thickness = 2 };
        Assert.Equal(RectInt.FromLTRB(-12, -12, 112, 12), a.GetBounds()); // HeadSize=12 默认外扩
        Assert.True(a.HitTest(new PointInt(50, 1), tolerance: 2));
        Assert.False(a.HitTest(new PointInt(50, 40), tolerance: 2));
    }

    [Fact]
    public void Freehand_Bounds_And_Hit()
    {
        var f = new FreehandAnnotation
        {
            Points = { new PointInt(0, 0), new PointInt(10, 10), new PointInt(0, 20) },
            Thickness = 2,
        };
        Assert.Equal(new RectInt(0, 0, 10, 20), f.GetBounds());
        Assert.True(f.HitTest(new PointInt(5, 5), tolerance: 2));   // 第一段上
        Assert.False(f.HitTest(new PointInt(50, 50), tolerance: 2));
    }

    [Fact]
    public void NumberStamp_Bounds_And_Hit()
    {
        var n = new NumberStampAnnotation { Center = new PointInt(50, 50), Radius = 10, Number = 3 };
        Assert.Equal(new RectInt(40, 40, 20, 20), n.GetBounds());
        Assert.True(n.HitTest(new PointInt(55, 50), tolerance: 0)); // 圆内
        Assert.False(n.HitTest(new PointInt(80, 50), tolerance: 0));
    }

    [Fact]
    public void Text_Bounds_AccountForWideCjkCharacters()
    {
        var text = new TextAnnotation
        {
            Position = new PointInt(0, 0),
            Text = "截图工具",
            FontSize = 20,
        };

        Assert.True(text.GetBounds().Width >= 80);
        Assert.True(text.HitTest(new PointInt(75, 10), tolerance: 0));
    }

    [Fact]
    public void Mosaic_DefaultsAndClone()
    {
        var m = new MosaicAnnotation { Rect = new RectInt(0, 0, 20, 20), BlockSize = 8, Mode = MosaicMode.Blur };
        var c = (MosaicAnnotation)m.Clone();
        Assert.Equal(MosaicMode.Blur, c.Mode);
        Assert.Equal(8, c.BlockSize);
        Assert.Equal(m.Id, c.Id);
    }

    [Fact]
    public void Clone_PreservesIdAndStyle_DeepCopiesGeometry()
    {
        var f = new FreehandAnnotation
        {
            Points = { new PointInt(1, 1), new PointInt(2, 2) },
            Stroke = new ColorRgba(1, 2, 3, 4),
            Thickness = 5,
            ZIndex = 7,
        };
        var c = (FreehandAnnotation)f.Clone();
        Assert.Equal(f.Id, c.Id);
        Assert.Equal(f.Stroke, c.Stroke);
        Assert.Equal(5, c.Thickness);
        Assert.Equal(7, c.ZIndex);

        // 深拷贝：改克隆体的点不影响原体
        c.Points.Add(new PointInt(9, 9));
        Assert.Equal(2, f.Points.Count);
        Assert.Equal(3, c.Points.Count);
    }

    [Fact]
    public void CopyStateFrom_AppliesGeometry_TypeChecked()
    {
        var live = new RectangleAnnotation { Rect = new RectInt(0, 0, 10, 10) };
        var snapshot = new RectangleAnnotation { Rect = new RectInt(5, 5, 20, 20), ZIndex = 3 };
        live.CopyStateFrom(snapshot);
        Assert.Equal(new RectInt(5, 5, 20, 20), live.Rect);
        Assert.Equal(3, live.ZIndex);

        // 类型不匹配抛出
        Assert.Throws<ArgumentException>(() => live.CopyStateFrom(new EllipseAnnotation()));
    }

    [Fact]
    public void Document_AddRemove_And_ZOrder()
    {
        var doc = new AnnotationDocument();
        var a = new RectangleAnnotation { ZIndex = 2, Rect = new RectInt(0, 0, 5, 5) };
        var b = new RectangleAnnotation { ZIndex = 0, Rect = new RectInt(0, 0, 5, 5) };
        var c = new RectangleAnnotation { ZIndex = 1, Rect = new RectInt(0, 0, 5, 5) };
        doc.Add(a);
        doc.Add(b);
        doc.Add(c);
        Assert.Equal(3, doc.Count);

        // 升序枚举（底->顶）：b(0), c(1), a(2)
        Assert.Equal(new[] { b, c, a }, doc.InZOrder().ToArray());

        Assert.True(doc.Remove(b));
        Assert.Equal(2, doc.Count);
        Assert.False(doc.Remove(b)); // 再删返回 false
    }

    [Fact]
    public void Document_AddSameReferenceTwice_Throws()
    {
        var doc = new AnnotationDocument();
        var a = new RectangleAnnotation { Rect = new RectInt(0, 0, 5, 5) };
        doc.Add(a);
        Assert.Throws<InvalidOperationException>(() => doc.Add(a));
    }

    [Fact]
    public void Document_AddSameIdTwice_Throws()
    {
        var doc = new AnnotationDocument();
        var a = new RectangleAnnotation { Rect = new RectInt(0, 0, 5, 5) };
        var b = new EllipseAnnotation { Id = a.Id, Rect = new RectInt(5, 5, 5, 5) };

        doc.Add(a);

        Assert.Throws<InvalidOperationException>(() => doc.Add(b));
    }

    [Fact]
    public void Document_HitTest_ReturnsTopmost()
    {
        var doc = new AnnotationDocument();
        // 两个重叠矩形，ZIndex 不同；命中应返回 ZIndex 更大者
        var bottom = new RectangleAnnotation { Rect = new RectInt(0, 0, 100, 100), ZIndex = 0, Thickness = 4 };
        var top = new RectangleAnnotation { Rect = new RectInt(0, 0, 100, 100), ZIndex = 5, Thickness = 4 };
        doc.Add(bottom);
        doc.Add(top);

        var hit = doc.HitTest(new PointInt(0, 50), tolerance: 2);
        Assert.Same(top, hit);
    }

    [Fact]
    public void Document_HitTest_SameZIndex_ReturnsLastAdded()
    {
        var doc = new AnnotationDocument();
        var first = new RectangleAnnotation { Rect = new RectInt(0, 0, 100, 100), ZIndex = 1, Thickness = 4 };
        var second = new RectangleAnnotation { Rect = new RectInt(0, 0, 100, 100), ZIndex = 1, Thickness = 4 };
        doc.Add(first);
        doc.Add(second);
        Assert.Same(second, doc.HitTest(new PointInt(0, 50), tolerance: 2));
    }

    [Fact]
    public void Document_HitTest_Miss_ReturnsNull()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectangleAnnotation { Rect = new RectInt(0, 0, 10, 10), Thickness = 2 });
        Assert.Null(doc.HitTest(new PointInt(500, 500), tolerance: 1));
    }

    [Fact]
    public void Document_BringToFront_RaisesZIndex()
    {
        var doc = new AnnotationDocument();
        var a = new RectangleAnnotation { ZIndex = 5, Rect = new RectInt(0, 0, 5, 5) };
        var b = new RectangleAnnotation { ZIndex = 2, Rect = new RectInt(0, 0, 5, 5) };
        doc.Add(a);
        doc.Add(b);
        doc.BringToFront(b);
        Assert.True(b.ZIndex > a.ZIndex);
    }

    [Fact]
    public void Document_BringToFront_NormalizesWhenZIndexNearOverflow()
    {
        var doc = new AnnotationDocument();
        var bottom = new RectangleAnnotation { ZIndex = int.MaxValue - 1, Rect = new RectInt(0, 0, 5, 5) };
        var top = new RectangleAnnotation { ZIndex = int.MaxValue, Rect = new RectInt(0, 0, 5, 5) };
        doc.Add(bottom);
        doc.Add(top);

        doc.BringToFront(bottom);

        Assert.True(bottom.ZIndex > top.ZIndex);
        Assert.NotEqual(int.MinValue, bottom.ZIndex);
        Assert.Equal(new AnnotationElement[] { top, bottom }, doc.InZOrder().ToArray());
    }

    [Fact]
    public void TextAnnotation_CjkBoundsUseWideCharacterWidth()
    {
        var text = new TextAnnotation
        {
            Position = new PointInt(10, 20),
            FontSize = 20,
            Text = "测试",
        };

        var bounds = text.GetBounds();

        Assert.Equal(40, bounds.Width);
        Assert.True(text.HitTest(new PointInt(49, 30), tolerance: 0));
    }

    [Fact]
    public void TextAnnotation_AsciiBoundsUseNarrowCharacterWidth()
    {
        var text = new TextAnnotation
        {
            Position = new PointInt(10, 20),
            FontSize = 20,
            Text = "aa",
        };

        var bounds = text.GetBounds();

        Assert.Equal(24, bounds.Width);
        Assert.False(text.HitTest(new PointInt(49, 30), tolerance: 0));
    }
}
