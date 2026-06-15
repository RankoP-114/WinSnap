using WinSnap.Core.Imaging;

namespace WinSnap.Core.Tests;

public class ToneMapperTests
{
    private const double ScrgbWhiteNits = 80.0;

    private static float[] OnePixel(float r, float g, float b, float a = 1f)
        => new[] { r, g, b, a };

    /// <summary>用给定绝对亮度（nits）构造一个灰色 scRGB 像素（scRGB 1.0 == 80 nits）。</summary>
    private static float[] GrayAtNits(double nits)
    {
        float v = (float)(nits / ScrgbWhiteNits);
        return OnePixel(v, v, v);
    }

    [Fact]
    public void PureBlack_MapsToZero()
    {
        var mapper = new ToneMapper();
        var outp = mapper.MapToSdrBgra(OnePixel(0, 0, 0), 1, 1, 300, 1000);
        Assert.Equal(0, outp[0]); // B
        Assert.Equal(0, outp[1]); // G
        Assert.Equal(0, outp[2]); // R
        Assert.Equal(255, outp[3]); // A 不透明
    }

    [Fact]
    public void SdrReferenceWhite_MapsToNear255()
    {
        var mapper = new ToneMapper();
        // SDR 参考白：亮度 = sdrWhiteNits
        var outp = mapper.MapToSdrBgra(GrayAtNits(300), 1, 1, 300, 1000);
        // 约 255（允许肩部带来的少量压低）
        Assert.True(outp[0] >= 250, $"B={outp[0]}");
        Assert.True(outp[1] >= 250, $"G={outp[1]}");
        Assert.True(outp[2] >= 250, $"R={outp[2]}");
    }

    [Fact]
    public void SuperBright_Highlight_ClampedNoOverflow()
    {
        var mapper = new ToneMapper();
        // scRGB 10.0 == 800 nits，仍应被压到 <=255 不溢出
        var outp = mapper.MapToSdrBgra(OnePixel(10f, 10f, 10f), 1, 1, 300, 1000);
        Assert.True(outp[0] <= 255);
        Assert.True(outp[2] <= 255);
        // 灰色高光映射后仍接近白（>=250）
        Assert.True(outp[2] >= 230, $"R={outp[2]}");
    }

    [Fact]
    public void MidHighlight_RollsOffBelowFullWhite()
    {
        var mapper = new ToneMapper();

        var white = mapper.MapToSdrBgra(GrayAtNits(300), 1, 1, 300, 1000);
        var highlight = mapper.MapToSdrBgra(GrayAtNits(500), 1, 1, 300, 1000);
        var brighterHighlight = mapper.MapToSdrBgra(GrayAtNits(800), 1, 1, 300, 1000);

        Assert.True(highlight[2] > white[2], $"highlight R={highlight[2]} 应 > white R={white[2]}");
        Assert.True(highlight[2] >= white[2] + 2, $"500nit 高光应与 SDR 白拉开至少 2 个码值，highlight R={highlight[2]} white R={white[2]}");
        Assert.True(highlight[2] < 255, $"500nit 高光不应硬剪裁到 255，R={highlight[2]}");
        Assert.True(brighterHighlight[2] > highlight[2], $"800nit 高光应继续高于 500nit，800nit R={brighterHighlight[2]} 500nit R={highlight[2]}");
        Assert.True(brighterHighlight[2] < 255, $"800nit 高光仍应低于峰值满白，R={brighterHighlight[2]}");
    }

    [Fact]
    public void NonFiniteInput_DoesNotMapBrightHighlightToBlack()
    {
        var mapper = new ToneMapper();

        var positiveInfinity = mapper.MapToSdrBgra(
            OnePixel(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity),
            1,
            1,
            300,
            1000);
        var nan = mapper.MapToSdrBgra(OnePixel(float.NaN, float.NaN, float.NaN), 1, 1, 300, 1000);

        Assert.True(positiveInfinity[2] >= 250, $"R={positiveInfinity[2]}");
        Assert.Equal(0, nan[2]);
        Assert.Equal(255, nan[3]);
    }

    [Fact]
    public void Output_AlwaysInByteRange()
    {
        var mapper = new ToneMapper();
        var rnd = new Random(42);
        const int w = 16, h = 16;
        var px = new float[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            // 覆盖 0..12 的 scRGB 范围（含超亮高光）
            px[i * 4 + 0] = (float)(rnd.NextDouble() * 12);
            px[i * 4 + 1] = (float)(rnd.NextDouble() * 12);
            px[i * 4 + 2] = (float)(rnd.NextDouble() * 12);
            px[i * 4 + 3] = 1f;
        }
        var outp = mapper.MapToSdrBgra(px, w, h, 300, 1000);
        Assert.Equal(w * h * 4, outp.Length);
        foreach (var v in outp)
            Assert.InRange(v, (byte)0, (byte)255); // byte 天然 0..255，断言长度/无异常
    }

    [Fact]
    public void Monotonic_BrighterInput_NotDarkerOutput()
    {
        var mapper = new ToneMapper();
        byte prev = 0;
        // 亮度从 0 增到很高，输出灰度应单调不减
        for (double nits = 0; nits <= 2000; nits += 25)
        {
            var outp = mapper.MapToSdrBgra(GrayAtNits(nits), 1, 1, 300, 1000);
            byte cur = outp[2]; // R
            Assert.True(cur >= prev, $"非单调：nits={nits} 时 R={cur} < 前值 {prev}");
            prev = cur;
        }
    }

    [Fact]
    public void HigherSdrWhite_DarkensMidGray()
    {
        var mapper = new ToneMapper();
        // 同一中灰（200nits），SDR White 设得越高，相对越暗（输出码值越低）
        var low = mapper.MapToSdrBgra(GrayAtNits(200), 1, 1, 200, 1000);
        var high = mapper.MapToSdrBgra(GrayAtNits(200), 1, 1, 400, 1000);
        Assert.True(high[2] <= low[2], $"high={high[2]} low={low[2]}");
    }

    [Fact]
    public void Peak_DoesNotExceed_White()
    {
        var mapper = new ToneMapper();
        // 恰为 HDR 峰值亮度的灰，输出不应溢出且接近白
        var outp = mapper.MapToSdrBgra(GrayAtNits(1000), 1, 1, 300, 1000);
        Assert.True(outp[2] <= 255);
        Assert.True(outp[2] >= 250, $"R={outp[2]}");
    }

    // ---- inputIsRec2020 参数（DXGI scRGB 路径，Rec.709 原色，跳过色域矩阵）----

    [Fact]
    public void Rec2020Flag_Default_PreservesLegacyGamutMatrix()
    {
        var mapper = new ToneMapper();
        // 中等亮度纯红：默认（Rec.2020→709）会用 1.66× 放大 R 通道；跳过矩阵则原样。
        // 选 sdrWhite=80（scRGB 白=1.0=80nits），令相对线性 rRel == scRGB 红值，避免肩部/钳顶干扰。
        var red = OnePixel(0.3f, 0f, 0f);
        var withMatrix = mapper.MapToSdrBgra(red, 1, 1, sdrWhiteNits: 80, hdrPeakNits: 80, inputIsRec2020: true);
        var noMatrix = mapper.MapToSdrBgra(red, 1, 1, sdrWhiteNits: 80, hdrPeakNits: 80, inputIsRec2020: false);

        // 默认值必须等价于显式传 true（向后兼容）。
        var defaultCall = mapper.MapToSdrBgra(red, 1, 1, sdrWhiteNits: 80, hdrPeakNits: 80);
        Assert.Equal(withMatrix[2], defaultCall[2]); // R
        Assert.Equal(withMatrix[1], defaultCall[1]); // G
        Assert.Equal(withMatrix[0], defaultCall[0]); // B

        // Rec.2020→709 对纯红 R 系数 1.66 > 1，故带矩阵的 R 应明显更亮；两路径必须不同。
        Assert.True(withMatrix[2] > noMatrix[2], $"withMatrix R={withMatrix[2]} 应 > noMatrix R={noMatrix[2]}");
        // 纯红两路径绿/蓝均应为 0（709 矩阵负系数被钳零）。
        Assert.Equal(0, noMatrix[1]);
        Assert.Equal(0, noMatrix[0]);
    }

    [Fact]
    public void Rec2020False_SdrWhitePoint_RoundTripsToWhite()
    {
        var mapper = new ToneMapper();
        // 模拟 SDR 屏 DXGI 路径：scRGB (1,1,1)=80nits，sdrWhite=peak=80 → 应近似原样还原为满白。
        var outp = mapper.MapToSdrBgra(OnePixel(1f, 1f, 1f), 1, 1, sdrWhiteNits: 80, hdrPeakNits: 80, inputIsRec2020: false);
        Assert.Equal(255, outp[2]); // R
        Assert.Equal(255, outp[1]); // G
        Assert.Equal(255, outp[0]); // B

        // 中性灰在跳过矩阵时也应保持中性（R==G==B）。
        var gray = mapper.MapToSdrBgra(OnePixel(0.5f, 0.5f, 0.5f), 1, 1, sdrWhiteNits: 80, hdrPeakNits: 80, inputIsRec2020: false);
        Assert.Equal(gray[2], gray[1]);
        Assert.Equal(gray[1], gray[0]);
    }

    [Fact]
    public void PixelApi_MatchesBulkMapper_ForDefaultBlackBoost()
    {
        var mapper = new ToneMapper();
        var px = OnePixel(2.4f, 1.2f, 0.35f, 0.75f);

        var bulk = mapper.MapToSdrBgra(
            px,
            1,
            1,
            sdrWhiteNits: 300,
            hdrPeakNits: 1000,
            inputIsRec2020: false);
        var pixel = ToneMapper.MapScRgbPixelToSdrBgra(
            px[0],
            px[1],
            px[2],
            px[3],
            sdrWhiteNits: 300,
            hdrPeakNits: 1000,
            inputIsRec2020: false);

        Assert.Equal(bulk[0], pixel.B);
        Assert.Equal(bulk[1], pixel.G);
        Assert.Equal(bulk[2], pixel.R);
        Assert.Equal(bulk[3], pixel.A);
    }

    [Fact]
    public void PrecomputedParameters_MatchPixelApiAndBulkMapper()
    {
        var mapper = new ToneMapper();
        var px = OnePixel(3.1f, 0.7f, 1.8f, 0.6f);
        var parameters = ToneMapper.CreateMappingParameters(
            sdrWhiteNits: 300,
            hdrPeakNits: 1000,
            inputIsRec2020: false);

        var bulk = mapper.MapToSdrBgra(
            px,
            1,
            1,
            sdrWhiteNits: 300,
            hdrPeakNits: 1000,
            inputIsRec2020: false);
        var validatedPixel = ToneMapper.MapScRgbPixelToSdrBgra(
            px[0],
            px[1],
            px[2],
            px[3],
            sdrWhiteNits: 300,
            hdrPeakNits: 1000,
            inputIsRec2020: false);
        var precomputedPixel = ToneMapper.MapScRgbPixelToSdrBgra(
            px[0],
            px[1],
            px[2],
            px[3],
            parameters);

        Assert.Equal(validatedPixel, precomputedPixel);
        Assert.Equal(bulk[0], precomputedPixel.B);
        Assert.Equal(bulk[1], precomputedPixel.G);
        Assert.Equal(bulk[2], precomputedPixel.R);
        Assert.Equal(bulk[3], precomputedPixel.A);
    }

    [Fact]
    public void PrecomputedParameters_SaturatedWhiteMapsTo255()
    {
        var parameters = ToneMapper.CreateMappingParameters(
            sdrWhiteNits: 80,
            hdrPeakNits: 80,
            inputIsRec2020: false);

        var pixel = ToneMapper.MapScRgbPixelToSdrBgra(
            1f,
            1f,
            1f,
            1f,
            parameters);

        Assert.Equal(255, pixel.R);
        Assert.Equal(255, pixel.G);
        Assert.Equal(255, pixel.B);
        Assert.Equal(255, pixel.A);
    }
}
