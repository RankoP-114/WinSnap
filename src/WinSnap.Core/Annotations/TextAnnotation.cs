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
    /// 估算包围盒。无字体度量，按近似字宽累计：行高 ≈ FontSize*1.4，
    /// ASCII 窄字符 ≈ FontSize*0.6，CJK/全角字符 ≈ FontSize，取各行最宽者。仅用于命中与脏区估计。
    /// </summary>
    public override RectInt GetBounds()
    {
        double lineHeight = FontSize * 1.4;
        double maxLineWidth = 0;
        int lines = 1;
        double currentLineWidth = 0;
        foreach (char ch in Text)
        {
            if (ch == '\n')
            {
                lines++;
                if (currentLineWidth > maxLineWidth) maxLineWidth = currentLineWidth;
                currentLineWidth = 0;
            }
            else
            {
                currentLineWidth += EstimateCharWidthFactor(ch);
            }
        }
        if (currentLineWidth > maxLineWidth) maxLineWidth = currentLineWidth;

        int w = (int)Math.Ceiling(Math.Max(1.0, maxLineWidth) * FontSize);
        int h = (int)Math.Ceiling(lines * lineHeight);
        return new RectInt(Position.X, Position.Y, w, h);
    }

    private static double EstimateCharWidthFactor(char ch)
    {
        if (ch == '\t')
            return 2.4;
        if (IsWideChar(ch))
            return 1.0;
        return 0.6;
    }

    private static bool IsWideChar(char ch)
        => ch is >= '\u2E80' and <= '\uA4CF'
            or >= '\uAC00' and <= '\uD7A3'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\uFE10' and <= '\uFE6F'
            or >= '\uFF01' and <= '\uFF60'
            or >= '\uFFE0' and <= '\uFFE6';

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
