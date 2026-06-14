namespace WinSnap.Core.Primitives;

/// <summary>
/// 平台无关的 32bpp 像素缓冲：字节序 BGRA、top-down、紧密排布（stride = Width * 4）。
/// 供长截图拼接（<c>ScrollStitcher</c>）与 HDR tone map 输出共用。
/// 字节布局与 WIC / GDI+ 的 32bppBGRA / PixelFormats.Bgra32 一致，便于 App 层零拷贝包装。
/// </summary>
public sealed class PixelBuffer
{
    /// <summary>每像素字节数（固定 4：B、G、R、A）。</summary>
    public const int BytesPerPixel = 4;

    /// <summary>宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>高度（像素）。</summary>
    public int Height { get; }

    /// <summary>BGRA 像素数据，长度 = Width * Height * 4。</summary>
    public byte[] Bgra { get; }

    /// <summary>行跨距（字节）= Width * 4。</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>分配指定尺寸的全零（透明黑）缓冲。</summary>
    public PixelBuffer(int width, int height)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        Bgra = new byte[checked(width * height * BytesPerPixel)];
    }

    /// <summary>包装现有 BGRA 字节数组（不拷贝）。要求长度恰为 width*height*4。</summary>
    public PixelBuffer(int width, int height, byte[] bgra)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(bgra);
        int expected = checked(width * height * BytesPerPixel);
        if (bgra.Length != expected)
            throw new ArgumentException(
                $"BGRA 长度应为 {expected}（{width}x{height}*4），实际 {bgra.Length}。", nameof(bgra));
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    /// <summary>某行的字节起始偏移。</summary>
    public int RowOffset(int y) => y * Stride;

    /// <summary>某像素的字节起始偏移（B 通道位置）。</summary>
    public int PixelOffset(int x, int y) => (y * Stride) + (x * BytesPerPixel);

    /// <summary>读取像素颜色。</summary>
    public ColorRgba GetPixel(int x, int y)
    {
        int i = PixelOffset(x, y);
        // 字节序 BGRA
        return new ColorRgba(Bgra[i + 2], Bgra[i + 1], Bgra[i], Bgra[i + 3]);
    }

    /// <summary>写入像素颜色。</summary>
    public void SetPixel(int x, int y, ColorRgba c)
    {
        int i = PixelOffset(x, y);
        Bgra[i] = c.B;
        Bgra[i + 1] = c.G;
        Bgra[i + 2] = c.R;
        Bgra[i + 3] = c.A;
    }

    /// <summary>取某一行的可读写视图（长度 = Stride）。</summary>
    public Span<byte> GetRowSpan(int y) => Bgra.AsSpan(RowOffset(y), Stride);

    /// <summary>
    /// 从源缓冲把一段连续行块拷贝到本缓冲指定行。
    /// 要求两者宽度相同。用于拼接时把新帧追加到结果底部。
    /// </summary>
    public void BlitRows(PixelBuffer source, int sourceStartRow, int destStartRow, int rowCount)
    {
        if (source.Width != Width)
            throw new ArgumentException("源与目标宽度必须一致。", nameof(source));
        if (rowCount <= 0) return;
        if (sourceStartRow < 0 || sourceStartRow + rowCount > source.Height)
            throw new ArgumentOutOfRangeException(nameof(sourceStartRow));
        if (destStartRow < 0 || destStartRow + rowCount > Height)
            throw new ArgumentOutOfRangeException(nameof(destStartRow));

        int bytes = rowCount * Stride;
        Buffer.BlockCopy(source.Bgra, source.RowOffset(sourceStartRow),
                         Bgra, RowOffset(destStartRow), bytes);
    }

    /// <summary>深拷贝。</summary>
    public PixelBuffer Clone() => new(Width, Height, (byte[])Bgra.Clone());
}
