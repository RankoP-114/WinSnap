namespace WinSnap.Interop;

/// <summary>
/// 一次屏幕捕获的原始像素数据，与 UI 框架无关。
/// 像素为 32bpp、BGRA 字节序、top-down 排列，<see cref="Stride"/> = Width*4。
/// 经 GDI 抓取时 Alpha 通道未定义（应按不透明 Bgr32 使用）。
/// </summary>
public sealed class CapturedImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] PixelsBgra { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public int Stride => Width * 4;

    public CapturedImage(int width, int height, byte[] pixelsBgra, IReadOnlyList<string>? diagnostics = null)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(pixelsBgra);

        long expectedBytes = (long)width * height * 4;
        if (expectedBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), $"图像过大，无法用单个 BGRA 缓冲表示：{width}x{height}。");
        if (pixelsBgra.LongLength < expectedBytes)
            throw new ArgumentException(
                $"BGRA 缓冲区太小，应至少为 {expectedBytes} 字节，实际 {pixelsBgra.LongLength} 字节。",
                nameof(pixelsBgra));

        Width = width;
        Height = height;
        PixelsBgra = pixelsBgra;
        Diagnostics = diagnostics is null ? Array.Empty<string>() : diagnostics.ToArray();
    }

    /// <summary>
    /// 粗略判断缓冲区里是否存在可见内容，用于过滤 Desktop Duplication 的空黑帧。
    /// 这是采样检测，不用于图像语义判断；纯黑桌面会返回 false。
    /// </summary>
    public bool HasVisibleContent(byte threshold = 6, int maxSamples = 20_000, int requiredHits = 8)
        => HasVisibleContent(Width, Height, PixelsBgra, threshold, maxSamples, requiredHits);

    public static bool HasVisibleContent(
        int width,
        int height,
        byte[] pixelsBgra,
        byte threshold = 6,
        int maxSamples = 20_000,
        int requiredHits = 8)
    {
        if (width <= 0 || height <= 0 || pixelsBgra.Length < 4)
            return false;

        long expectedPixels = (long)width * height;
        long availablePixels = pixelsBgra.LongLength / 4;
        long pixelCount = Math.Min(expectedPixels, availablePixels);
        if (pixelCount <= 0)
            return false;

        long step = Math.Max(1, pixelCount / Math.Max(1, maxSamples));
        int hits = 0;
        for (long p = 0; p < pixelCount; p += step)
        {
            int i = (int)(p * 4);
            byte b = pixelsBgra[i];
            byte g = pixelsBgra[i + 1];
            byte r = pixelsBgra[i + 2];
            if (Math.Max(r, Math.Max(g, b)) > threshold && ++hits >= requiredHits)
                return true;
        }
        return false;
    }
}
