using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WinSnap.Core.Annotations;
using WinSnap.Core.Primitives;

namespace WinSnap.App.Annotation;

/// <summary>把平台无关的 <see cref="AnnotationElement"/> 渲染到 WPF DrawingContext（物理像素坐标）。</summary>
public static class AnnotationRenderer
{
    public static void Render(DrawingContext dc, AnnotationElement element)
    {
        var color = Color.FromArgb(element.Stroke.A, element.Stroke.R, element.Stroke.G, element.Stroke.B);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, element.Thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();

        switch (element)
        {
            case RectangleAnnotation r:
                dc.DrawRectangle(null, pen, ToRect(r.Rect));
                break;
            case EllipseAnnotation e:
                var rc = ToRect(e.Rect);
                dc.DrawEllipse(null, pen, new Point(rc.X + rc.Width / 2, rc.Y + rc.Height / 2),
                    rc.Width / 2, rc.Height / 2);
                break;
            case ArrowAnnotation a:
                DrawArrow(dc, pen, brush, a);
                break;
            case FreehandAnnotation f:
                DrawFreehand(dc, pen, f);
                break;
            case TextAnnotation t:
                DrawText(dc, brush, t);
                break;
            case NumberStampAnnotation n:
                DrawNumberStamp(dc, brush, n);
                break;
        }
    }

    private static void DrawNumberStamp(DrawingContext dc, Brush brush, NumberStampAnnotation n)
    {
        var center = new Point(n.Center.X, n.Center.Y);
        double radius = Math.Max(8, n.Radius);
        dc.DrawEllipse(brush, null, center, radius, radius);

        var ft = new FormattedText(
            n.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            radius * 1.25,
            Brushes.White,
            1.0);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    private static Rect ToRect(RectInt r)
    {
        var n = r.Normalized();
        return new Rect(n.X, n.Y, n.Width, n.Height);
    }

    private static void DrawArrow(DrawingContext dc, Pen pen, Brush brush, ArrowAnnotation a)
    {
        var start = new Point(a.Start.X, a.Start.Y);
        var end = new Point(a.End.X, a.End.Y);
        dc.DrawLine(pen, start, end);

        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        double size = Math.Max(8, a.HeadSize);
        var p1 = new Point(end.X - size * Math.Cos(angle - Math.PI / 7),
                           end.Y - size * Math.Sin(angle - Math.PI / 7));
        var p2 = new Point(end.X - size * Math.Cos(angle + Math.PI / 7),
                           end.Y - size * Math.Sin(angle + Math.PI / 7));

        var figure = new PathFigure { StartPoint = end, IsClosed = true };
        figure.Segments.Add(new LineSegment(p1, false));
        figure.Segments.Add(new LineSegment(p2, false));
        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        dc.DrawGeometry(brush, null, geo);
    }

    private static void DrawFreehand(DrawingContext dc, Pen pen, FreehandAnnotation f)
    {
        if (f.Points.Count < 2)
            return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(f.Points[0].X, f.Points[0].Y), false, false);
            for (int i = 1; i < f.Points.Count; i++)
                ctx.LineTo(new Point(f.Points[i].X, f.Points[i].Y), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static void DrawText(DrawingContext dc, Brush brush, TextAnnotation t)
    {
        if (string.IsNullOrEmpty(t.Text))
            return;

        var ft = new FormattedText(
            t.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            t.FontSize,
            brush,
            1.0);
        dc.DrawText(ft, new Point(t.Position.X, t.Position.Y));
    }
}
