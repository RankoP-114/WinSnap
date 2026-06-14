namespace WinSnap.Core.Primitives;

/// <summary>
/// 平台无关的 32bpp 颜色（每通道 8bit，非预乘）。值语义。
/// 通道顺序为逻辑 R/G/B/A；与像素缓冲的字节布局（BGRA）相互转换见辅助方法。
/// </summary>
public readonly record struct ColorRgba(byte R, byte G, byte B, byte A)
{
    public static ColorRgba Transparent => new(0, 0, 0, 0);
    public static ColorRgba Black => new(0, 0, 0, 255);
    public static ColorRgba White => new(255, 255, 255, 255);
    public static ColorRgba Red => new(255, 0, 0, 255);

    /// <summary>不透明构造（A=255）。</summary>
    public static ColorRgba FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

    /// <summary>从 0xAARRGGBB 打包整数构造。</summary>
    public static ColorRgba FromArgb(uint argb)
        => new(
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF),
            (byte)((argb >> 24) & 0xFF));

    /// <summary>打包为 0xAARRGGBB。</summary>
    public uint ToArgb()
        => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary>返回替换 Alpha 后的副本。</summary>
    public ColorRgba WithAlpha(byte a) => this with { A = a };

    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}
