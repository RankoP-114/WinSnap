using System.Runtime.InteropServices;

namespace WinSnap.Core.Imaging;

/// <summary>
/// HDR scRGB（线性 FP16，(1,1,1)=D65 白 80 nits，可超 1.0）→ SDR sRGB 8bit BGRA 的色调映射器。
///
/// 采用 <b>maxRGB</b> 色调映射 + <b>单调 shoulder 滚降</b>（含 black boost）保色相压高光：
/// ① 按 scRGB 语义把线性值换算为绝对亮度（value × 80 nits），再以 <c>sdrWhiteNits</c> 归一化为
///    「相对 SDR 白」的线性光（SDR 参考白 = 1.0）；
/// ② 取 maxRGB 走 EETF：膝点以下恒等直通，膝点以上用幂函数 shoulder 把源峰值
///    <c>hdrPeakNits</c>（归一后 = hdrPeak/sdrWhite）单调滚降到 1.0，按增益等比缩放 RGB
///    （maxRGB 法 → 保色相，不溢出，单调）；black boost 抬升极暗部；
/// ③ Rec.2020→Rec.709 色域 3×3 矩阵；
/// ④ sRGB OETF（gamma）编码为 8bit，输出 BGRA。
///
/// 设计取舍：BT.2390 字面定义工作在 PQ 绝对域，但那会令 SDR 参考白本身落入肩部被压暗。
/// 本实现把 EETF 工作域选在「以 SDR 白归一化的相对线性光」上，膝点略低于 SDR 白，
/// 令 SDR 白保持接近满白，同时给白点以上高光留下可见滚降空间。纯托管、无原生依赖，便于单测。
/// </summary>
public sealed class ToneMapper
{
    private const double ScrgbWhiteNits = 80.0; // scRGB: 线性 1.0 == 80 nits
    private const double KneeStart = 0.95;
    private const int SrgbOetfLookupMax = 65535;
    private static readonly byte[] SrgbOetfByteLookup = BuildSrgbOetfByteLookup();
    private static readonly float[] HalfToFloatLookup = BuildHalfToFloatLookup();

    /// <summary>默认 SDR 参考白亮度（nits）。</summary>
    public const double DefaultSdrWhiteNits = 300.0;

    /// <summary>默认 HDR 峰值亮度（nits）。</summary>
    public const double DefaultHdrPeakNits = 1000.0;

    /// <summary>极暗部 black-boost 抬升量（相对输出亮度，0 表示不抬升）。</summary>
    public double BlackBoost { get; init; }

    /// <summary>预计算后的 tone-map 参数。用于逐像素/流式路径避免重复做参数校验和常量除法。</summary>
    public readonly record struct MappingParameters(
        double XPeak,
        double ScaleScrgbToRel,
        bool InputIsRec2020,
        double BlackBoost);

    /// <summary>创建可复用的 tone-map 参数。适合 Desktop Duplication 逐像素流式转换路径。</summary>
    public static MappingParameters CreateMappingParameters(
        double sdrWhiteNits = DefaultSdrWhiteNits,
        double hdrPeakNits = DefaultHdrPeakNits,
        bool inputIsRec2020 = true,
        double blackBoost = 0.0)
    {
        if (!IsPositiveFinite(sdrWhiteNits)) throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits));
        if (!IsPositiveFinite(hdrPeakNits)) throw new ArgumentOutOfRangeException(nameof(hdrPeakNits));
        if (hdrPeakNits < sdrWhiteNits) hdrPeakNits = sdrWhiteNits; // peak 不应低于白点

        return new MappingParameters(
            XPeak: hdrPeakNits / sdrWhiteNits,
            ScaleScrgbToRel: ScrgbWhiteNits / sdrWhiteNits,
            InputIsRec2020: inputIsRec2020,
            BlackBoost: blackBoost);
    }

    /// <summary>
    /// 把一整幅 scRGB 线性像素映射为 SDR sRGB 8bit BGRA（top-down, stride=width*4）。
    /// </summary>
    /// <param name="scRgbaLinear">线性 scRGB 像素，按 R,G,B,A 顺序，长度 = width*height*4。</param>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    /// <param name="sdrWhiteNits">SDR 参考白亮度（nits），映射到输出满白，默认见 <see cref="DefaultSdrWhiteNits"/>。</param>
    /// <param name="hdrPeakNits">源 HDR 峰值亮度（nits），肩部滚降的源白点，默认见 <see cref="DefaultHdrPeakNits"/>。</param>
    /// <param name="inputIsRec2020">
    /// 输入原色是否为 Rec.2020。<b>默认 true</b>（保持历史行为）：执行 Rec.2020→Rec.709 色域 3×3 矩阵。
    /// 传 <c>false</c> 时跳过该矩阵——适用于 DXGI Desktop Duplication 的 scRGB
    /// （<c>R16G16B16A16_FLOAT</c>）数据，其原色本就是 Rec.709/sRGB，无需再做色域转换。
    /// </param>
    /// <returns>BGRA 字节数组，长度 = width*height*4，每分量 0–255。</returns>
    public byte[] MapToSdrBgra(
        ReadOnlySpan<float> scRgbaLinear,
        int width,
        int height,
        double sdrWhiteNits = DefaultSdrWhiteNits,
        double hdrPeakNits = DefaultHdrPeakNits,
        bool inputIsRec2020 = true)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        long expected = (long)width * height * 4;
        if (scRgbaLinear.Length != expected)
            throw new ArgumentException(
                $"输入长度应为 {expected}（{width}x{height}*4），实际 {scRgbaLinear.Length}。",
                nameof(scRgbaLinear));
        if (expected > int.MaxValue)
            throw new InvalidOperationException($"HDR 图像过大，无法展开为 SDR 缓冲：{width}x{height}。");

        var dst = new byte[(int)expected];
        MapToSdrBgra(scRgbaLinear, dst, width, height, sdrWhiteNits, hdrPeakNits, inputIsRec2020);
        return dst;
    }

    /// <summary>
    /// 把一整幅 scRGB 线性像素映射并写入调用方提供的 BGRA 缓冲，避免额外分配输出数组。
    /// </summary>
    public void MapToSdrBgra(
        ReadOnlySpan<float> scRgbaLinear,
        Span<byte> destinationBgra,
        int width,
        int height,
        double sdrWhiteNits = DefaultSdrWhiteNits,
        double hdrPeakNits = DefaultHdrPeakNits,
        bool inputIsRec2020 = true)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        long expected = (long)width * height * 4;
        if (expected > int.MaxValue)
            throw new InvalidOperationException($"HDR 图像过大，无法展开为 SDR 缓冲：{width}x{height}。");
        if (scRgbaLinear.Length != expected)
            throw new ArgumentException(
                $"输入长度应为 {expected}（{width}x{height}*4），实际 {scRgbaLinear.Length}。",
                nameof(scRgbaLinear));
        if (destinationBgra.Length < expected)
            throw new ArgumentException("目标 BGRA 缓冲区太小。", nameof(destinationBgra));

        var parameters = CreateMappingParameters(sdrWhiteNits, hdrPeakNits, inputIsRec2020, BlackBoost);
        MapScRgbPixelsToSdrBgra(scRgbaLinear, destinationBgra[..(int)expected], parameters);
    }

    /// <summary>使用预计算参数批量映射 scRGB 像素到 SDR BGRA。输入/输出均为紧密 RGBA/BGRA 排列。</summary>
    public static void MapScRgbPixelsToSdrBgra(
        ReadOnlySpan<float> scRgbaLinear,
        Span<byte> destinationBgra,
        MappingParameters parameters)
    {
        if (scRgbaLinear.Length % 4 != 0)
            throw new ArgumentException("输入 RGBA 缓冲长度必须是 4 的倍数。", nameof(scRgbaLinear));
        if (destinationBgra.Length < scRgbaLinear.Length)
            throw new ArgumentException("目标 BGRA 缓冲区太小。", nameof(destinationBgra));

        if (parameters.InputIsRec2020)
            MapScRgbPixelsToSdrBgraRec2020(scRgbaLinear, destinationBgra, parameters);
        else
            MapScRgbPixelsToSdrBgra709(scRgbaLinear, destinationBgra, parameters);
    }

    /// <summary>使用预计算参数批量映射 FP16 scRGB 像素到 SDR BGRA。输入/输出均为紧密 RGBA/BGRA 排列。</summary>
    public static void MapScRgbHalfPixelsToSdrBgra(
        ReadOnlySpan<Half> scRgbaLinear,
        Span<byte> destinationBgra,
        MappingParameters parameters)
    {
        if (scRgbaLinear.Length % 4 != 0)
            throw new ArgumentException("输入 FP16 RGBA 缓冲长度必须是 4 的倍数。", nameof(scRgbaLinear));
        if (destinationBgra.Length < scRgbaLinear.Length)
            throw new ArgumentException("目标 BGRA 缓冲区太小。", nameof(destinationBgra));

        ReadOnlySpan<ushort> halfBits = MemoryMarshal.Cast<Half, ushort>(scRgbaLinear);
        if (parameters.InputIsRec2020)
            MapScRgbHalfPixelsToSdrBgraRec2020(halfBits, destinationBgra, parameters);
        else
            MapScRgbHalfPixelsToSdrBgra709(halfBits, destinationBgra, parameters);
    }

    private static void MapScRgbPixelsToSdrBgra709(
        ReadOnlySpan<float> scRgbaLinear,
        Span<byte> destinationBgra,
        MappingParameters parameters)
    {
        int pixelCount = scRgbaLinear.Length / 4;
        for (int i = 0; i < pixelCount; i++)
        {
            int s = i * 4;
            WriteScRgbPixelToSdrBgra709(
                scRgbaLinear[s],
                scRgbaLinear[s + 1],
                scRgbaLinear[s + 2],
                scRgbaLinear[s + 3],
                parameters,
                destinationBgra,
                s);
        }
    }

    private static void MapScRgbPixelsToSdrBgraRec2020(
        ReadOnlySpan<float> scRgbaLinear,
        Span<byte> destinationBgra,
        MappingParameters parameters)
    {
        int pixelCount = scRgbaLinear.Length / 4;
        for (int i = 0; i < pixelCount; i++)
        {
            int s = i * 4;
            WriteScRgbPixelToSdrBgraRec2020(
                scRgbaLinear[s],
                scRgbaLinear[s + 1],
                scRgbaLinear[s + 2],
                scRgbaLinear[s + 3],
                parameters,
                destinationBgra,
                s);
        }
    }

    private static void MapScRgbHalfPixelsToSdrBgra709(
        ReadOnlySpan<ushort> halfBits,
        Span<byte> destinationBgra,
        MappingParameters parameters)
    {
        int pixelCount = halfBits.Length / 4;
        for (int i = 0; i < pixelCount; i++)
        {
            int s = i * 4;
            WriteScRgbPixelToSdrBgra709(
                HalfToFloatLookup[halfBits[s]],
                HalfToFloatLookup[halfBits[s + 1]],
                HalfToFloatLookup[halfBits[s + 2]],
                HalfToFloatLookup[halfBits[s + 3]],
                parameters,
                destinationBgra,
                s);
        }
    }

    private static void MapScRgbHalfPixelsToSdrBgraRec2020(
        ReadOnlySpan<ushort> halfBits,
        Span<byte> destinationBgra,
        MappingParameters parameters)
    {
        int pixelCount = halfBits.Length / 4;
        for (int i = 0; i < pixelCount; i++)
        {
            int s = i * 4;
            WriteScRgbPixelToSdrBgraRec2020(
                HalfToFloatLookup[halfBits[s]],
                HalfToFloatLookup[halfBits[s + 1]],
                HalfToFloatLookup[halfBits[s + 2]],
                HalfToFloatLookup[halfBits[s + 3]],
                parameters,
                destinationBgra,
                s);
        }
    }

    /// <summary>
    /// 映射单个 scRGB 像素到 SDR BGRA。供流式/局部捕获路径复用同一套 tone map 常量与公式，
    /// 避免为了处理子矩形而复制 <see cref="ToneMapper"/> 的内部实现。
    /// </summary>
    public static (byte B, byte G, byte R, byte A) MapScRgbPixelToSdrBgra(
        float rLinear,
        float gLinear,
        float bLinear,
        float aLinear,
        double sdrWhiteNits = DefaultSdrWhiteNits,
        double hdrPeakNits = DefaultHdrPeakNits,
        bool inputIsRec2020 = true)
    {
        var parameters = CreateMappingParameters(sdrWhiteNits, hdrPeakNits, inputIsRec2020);

        return MapScRgbPixelToSdrBgraCore(
            rLinear,
            gLinear,
            bLinear,
            aLinear,
            parameters);
    }

    /// <summary>使用预计算参数映射单个 scRGB 像素到 SDR BGRA。调用方负责复用同一组参数。</summary>
    public static (byte B, byte G, byte R, byte A) MapScRgbPixelToSdrBgra(
        float rLinear,
        float gLinear,
        float bLinear,
        float aLinear,
        MappingParameters parameters)
    {
        return MapScRgbPixelToSdrBgraCore(rLinear, gLinear, bLinear, aLinear, parameters);
    }

    /// <summary>使用预计算参数映射单个 scRGB 像素，并直接写入 4 字节 BGRA 缓冲。</summary>
    public static void MapScRgbPixelToSdrBgra(
        float rLinear,
        float gLinear,
        float bLinear,
        float aLinear,
        MappingParameters parameters,
        Span<byte> destinationBgra)
    {
        if (destinationBgra.Length < 4)
            throw new ArgumentException("目标 BGRA 缓冲区太小。", nameof(destinationBgra));

        if (parameters.InputIsRec2020)
            WriteScRgbPixelToSdrBgraRec2020(rLinear, gLinear, bLinear, aLinear, parameters, destinationBgra, 0);
        else
            WriteScRgbPixelToSdrBgra709(rLinear, gLinear, bLinear, aLinear, parameters, destinationBgra, 0);
    }

    private static (byte B, byte G, byte R, byte A) MapScRgbPixelToSdrBgraCore(
        float rLinear,
        float gLinear,
        float bLinear,
        float aLinear,
        MappingParameters parameters)
    {
        Span<byte> bgra = stackalloc byte[4];
        if (parameters.InputIsRec2020)
            WriteScRgbPixelToSdrBgraRec2020(rLinear, gLinear, bLinear, aLinear, parameters, bgra, 0);
        else
            WriteScRgbPixelToSdrBgra709(rLinear, gLinear, bLinear, aLinear, parameters, bgra, 0);

        return (bgra[0], bgra[1], bgra[2], bgra[3]);
    }

    private static void WriteScRgbPixelToSdrBgra709(
        float rLinear,
        float gLinear,
        float bLinear,
        float aLinear,
        MappingParameters parameters,
        Span<byte> destinationBgra,
        int destinationOffset)
    {
        MapScRgbToToneMappedRelative(rLinear, gLinear, bLinear, parameters, out double rRel, out double gRel, out double bRel);
        WriteSdrBgra(rRel, gRel, bRel, aLinear, destinationBgra, destinationOffset);
    }

    private static void WriteScRgbPixelToSdrBgraRec2020(
        float rLinear,
        float gLinear,
        float bLinear,
        float aLinear,
        MappingParameters parameters,
        Span<byte> destinationBgra,
        int destinationOffset)
    {
        MapScRgbToToneMappedRelative(rLinear, gLinear, bLinear, parameters, out double rRel, out double gRel, out double bRel);

        // Rec.2020 → Rec.709 色域。
        double r709 = (1.6604910 * rRel) - (0.5876411 * gRel) - (0.0728499 * bRel);
        double g709 = (-0.1245505 * rRel) + (1.1328999 * gRel) - (0.0083494 * bRel);
        double b709 = (-0.0181508 * rRel) - (0.1005789 * gRel) + (1.1187297 * bRel);
        WriteSdrBgra(r709, g709, b709, aLinear, destinationBgra, destinationOffset);
    }

    private static void MapScRgbToToneMappedRelative(
        float rLinear,
        float gLinear,
        float bLinear,
        MappingParameters parameters,
        out double rRel,
        out double gRel,
        out double bRel)
    {
        // ① scRGB → 相对 SDR 白线性（白点 = 1.0）
        rRel = ToRelativeSdrWhite(rLinear, parameters.ScaleScrgbToRel, parameters.XPeak);
        gRel = ToRelativeSdrWhite(gLinear, parameters.ScaleScrgbToRel, parameters.XPeak);
        bRel = ToRelativeSdrWhite(bLinear, parameters.ScaleScrgbToRel, parameters.XPeak);

        // ② maxRGB EETF：对峰值通道求肩部压缩增益，等比缩放（保色相）
        double maxRel = Math.Max(rRel, Math.Max(gRel, bRel));
        if (maxRel > 1e-9)
        {
            double mapped = Eetf(maxRel, KneeStart, parameters.XPeak, parameters.BlackBoost);
            double gain = mapped / maxRel;
            rRel *= gain;
            gRel *= gain;
            bRel *= gain;
        }
    }

    private static void WriteSdrBgra(
        double r709,
        double g709,
        double b709,
        float aLinear,
        Span<byte> destinationBgra,
        int destinationOffset)
    {
        // sRGB OETF → 8bit
        destinationBgra[destinationOffset] = ToSrgbByte(b709);
        destinationBgra[destinationOffset + 1] = ToSrgbByte(g709);
        destinationBgra[destinationOffset + 2] = ToSrgbByte(r709);
        destinationBgra[destinationOffset + 3] = ToByte(Clamp01OrDefault(aLinear, fallback: 1.0));
    }

    /// <summary>
    /// 相对线性域的 maxRGB EETF：把相对 SDR 白亮度 <paramref name="x"/>（白=1）压到 [0,1]。
    /// x ≤ <paramref name="ks"/> 恒等；x &gt; ks 用单调 shoulder 把 [ks, xPeak] 滚降到 [ks, 1]；
    /// 末尾施加 black boost。
    /// </summary>
    private static double Eetf(double x, double ks, double xPeak, double blackBoost)
    {
        if (double.IsNaN(x) || x <= 0.0) return 0.0;
        if (double.IsPositiveInfinity(x)) return 1.0;

        double y;
        if (x <= ks || xPeak <= ks)
        {
            y = Math.Min(x, 1.0);
        }
        else if (x >= xPeak)
        {
            y = 1.0; // 峰值及以上 → 满白，不溢出
        }
        else
        {
            double t = (x - ks) / (xPeak - ks);
            y = ks + ((1.0 - ks) * Math.Sqrt(t));
        }

        // black boost（BT.2390 形式）：minLum*(1-y)^4
        if (blackBoost > 0.0)
        {
            double inv = 1.0 - y;
            y += blackBoost * inv * inv * inv * inv;
        }

        return Clamp01(y);
    }

    /// <summary>sRGB OETF：线性 [0,1] → sRGB 编码 [0,1]。</summary>
    private static double SrgbOetf(double c)
    {
        if (c <= 0.0031308) return 12.92 * c;
        if (c >= 1.0) return 1.0;
        return (1.055 * Math.Pow(c, 1.0 / 2.4)) - 0.055;
    }

    private static byte ToSrgbByte(double linear)
    {
        if (double.IsNaN(linear) || linear <= 0.0)
            return 0;
        if (double.IsPositiveInfinity(linear) || linear >= 1.0)
            return 255;

        int index = (int)Math.Round(linear * SrgbOetfLookupMax);
        return SrgbOetfByteLookup[index];
    }

    private static byte[] BuildSrgbOetfByteLookup()
    {
        var lookup = new byte[SrgbOetfLookupMax + 1];
        for (int i = 0; i < lookup.Length; i++)
        {
            double linear = i / (double)SrgbOetfLookupMax;
            lookup[i] = ToByte(SrgbOetf(linear));
        }

        return lookup;
    }

    private static float[] BuildHalfToFloatLookup()
    {
        var lookup = new float[ushort.MaxValue + 1];
        for (int i = 0; i < lookup.Length; i++)
            lookup[i] = (float)BitConverter.UInt16BitsToHalf((ushort)i);
        return lookup;
    }

    private static bool IsPositiveFinite(double v)
        => v > 0.0 && !double.IsNaN(v) && !double.IsInfinity(v);

    private static double ToRelativeSdrWhite(float value, double scaleScrgbToRel, double xPeak)
    {
        double v = value;
        if (double.IsNaN(v) || v <= 0.0 || double.IsNegativeInfinity(v))
            return 0.0;
        if (double.IsPositiveInfinity(v))
            return xPeak;

        return v * scaleScrgbToRel;
    }

    private static double Clamp01(double v)
    {
        if (double.IsNaN(v) || v <= 0.0) return 0.0;
        if (double.IsPositiveInfinity(v) || v >= 1.0) return 1.0;
        return v;
    }

    private static double Clamp01OrDefault(double v, double fallback)
    {
        if (double.IsNaN(v)) return Clamp01(fallback);
        return Clamp01(v);
    }

    private static byte ToByte(double v01)
    {
        double scaled = (v01 * 255.0) + 0.5; // 四舍五入
        if (scaled <= 0.0) return 0;
        if (scaled >= 255.0) return 255;
        return (byte)scaled;
    }
}
