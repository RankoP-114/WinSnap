using WinSnap.Core.Primitives;

namespace WinSnap.Core.Annotations;

/// <summary>
/// 文本标注。<see cref="Position"/> 为文本左上角锚点；包围盒按字符数与字号粗略估算
/// （Core 不做字体度量，精确测量交由 App 层渲染时回填）。
/// </summary>
public sealed class TextAnnotation : AnnotationElement
{
    public PointInt Position { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>字号（像素 / DIP）。</summary>
    public double FontSize { get; set; } = 16.0;

    /// <summary>
    /// 估算包围盒。无字体度量，按等宽近似：行高 ≈ FontSize*1.4，
    /// 字宽 ≈ FontSize*0.6，取各行最长者。仅用于命中与脏区估计。
    /// </summary>
    public override RectInt GetBounds()
    {
        double lineHeight = FontSize * 1.4;
        double charWidth = FontSize * 0.6;
        int maxLineLen = 0;
        int lines = 1;
        int cur = 0;
        foreach (char ch in Text)
        {
            if (ch == '\n')
            {
                lines++;
                if (cur > maxLineLen) maxLineLen = cur;
                cur = 0;
            }
            else
            {
                cur++;
            }
        }
        if (cur > maxLineLen) maxLineLen = cur;

        int w = (int)Math.Ceiling(Math.Max(1, maxLineLen) * charWidth);
        int h = (int)Math.Ceiling(lines * lineHeight);
        return new RectInt(Position.X, Position.Y, w, h);
    }

    public override bool HitTest(PointInt p, double tolerance)
    {
        int tol = (int)Math.Ceiling(EffectiveTolerance(tolerance));
        return GetBounds().Inflate(tol).Contains(p);
    }

    public override AnnotationElement Clone()
    {
        var c = new TextAnnotation { Position = Position, Text = Text, FontSize = FontSize };
        CopyBaseTo(c);
        return c;
    }

    protected override void CopyGeometryFrom(AnnotationElement source)
    {
        var s = (TextAnnotation)source;
        Position = s.Position;
        Text = s.Text;
        FontSize = s.FontSize;
    }
}
