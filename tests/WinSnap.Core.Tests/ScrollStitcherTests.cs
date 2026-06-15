using WinSnap.Core.Primitives;
using WinSnap.Core.Stitching;

namespace WinSnap.Core.Tests;

public class ScrollStitcherTests
{
    private const int Width = 40;

    /// <summary>
    /// 生成确定性的"长图"：每行颜色由行号哈希决定，且横向用列索引调制，
    /// 保证任意窄竖直条带在竖直方向具有唯一最佳匹配（无平移二义性）。
    /// </summary>
    private static PixelBuffer MakeLongImage(int height, int seed = 12345)
    {
        var buf = new PixelBuffer(Width, height);
        for (int y = 0; y < height; y++)
        {
            // 行特征
            uint hr = Hash((uint)(y + seed) * 2654435761u);
            byte rowR = (byte)(hr & 0xFF);
            byte rowG = (byte)((hr >> 8) & 0xFF);
            byte rowB = (byte)((hr >> 16) & 0xFF);
            for (int x = 0; x < Width; x++)
            {
                // 横向调制（与列相关），让每行内部也有结构
                byte r = (byte)(rowR ^ (x * 7));
                byte g = (byte)(rowG ^ (x * 13));
                byte b = (byte)(rowB ^ (x * 31));
                buf.SetPixel(x, y, new ColorRgba(r, g, b, 255));
            }
        }
        return buf;
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    /// <summary>从长图截取 [startRow, startRow+frameHeight) 作为一帧。</summary>
    private static PixelBuffer Slice(PixelBuffer img, int startRow, int frameHeight)
    {
        var f = new PixelBuffer(Width, frameHeight);
        f.BlitRows(img, startRow, 0, frameHeight);
        return f;
    }

    private static void AssertImagesEqual(PixelBuffer expected, PixelBuffer actual, int tolerance = 0)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                var e = expected.GetPixel(x, y);
                var a = actual.GetPixel(x, y);
                Assert.True(
                    Math.Abs(e.R - a.R) <= tolerance &&
                    Math.Abs(e.G - a.G) <= tolerance &&
                    Math.Abs(e.B - a.B) <= tolerance,
                    $"像素 ({x},{y}) 不一致：期望 {e}，实际 {a}");
            }
        }
    }

    [Fact]
    public void Stitch_OverlappingFrames_ReconstructsFullImage()
    {
        const int totalHeight = 400;
        const int frameHeight = 150;
        const int step = 60; // 重叠 90 行

        var longImg = MakeLongImage(totalHeight);

        var stitcher = new ScrollStitcher(templateHeight: 80);

        int start = 0;
        int lastStart = totalHeight - frameHeight;
        while (true)
        {
            int s = Math.Min(start, lastStart);
            stitcher.Append(Slice(longImg, s, frameHeight));
            if (s >= lastStart) break;
            start += step;
        }

        var result = stitcher.Build();
        Assert.Equal(totalHeight, stitcher.CurrentHeight);
        Assert.Equal(totalHeight, result.Height);
        AssertImagesEqual(longImg, result);
    }

    [Fact]
    public void Stitch_FirstFrame_IsBaseline()
    {
        var img = MakeLongImage(100);
        var stitcher = new ScrollStitcher();
        var frame = Slice(img, 0, 100);
        stitcher.Append(frame);
        Assert.Equal(100, stitcher.CurrentHeight);
        Assert.Equal(1, stitcher.FrameCount);
        Assert.Equal(100, stitcher.LastAppendedHeight);
        AssertImagesEqual(frame, stitcher.Build());
    }

    [Fact]
    public void Stitch_KnownOffset_AppendsExactNewRows()
    {
        // 长图 200 行；两帧各 120 行，第二帧相对第一帧下移 50 行。
        var img = MakeLongImage(200);
        var stitcher = new ScrollStitcher(templateHeight: 60);
        stitcher.Append(Slice(img, 0, 120));   // 行 [0,120)
        stitcher.Append(Slice(img, 50, 120));  // 行 [50,170)，新增应为 50 行 -> 总高 170

        Assert.Equal(170, stitcher.CurrentHeight);
        Assert.Equal(50, stitcher.LastAppendedHeight);
        AssertImagesEqual(Slice(img, 0, 170), stitcher.Build());
    }

    [Fact]
    public void Stitch_RepeatedSameFrame_DetectsBottom()
    {
        var img = MakeLongImage(150);
        var stitcher = new ScrollStitcher(templateHeight: 60);
        var frame = Slice(img, 0, 120);
        stitcher.Append(frame);
        Assert.False(stitcher.IsAtBottom);

        // 再次追加完全相同的帧：无新增内容 -> 到底
        stitcher.Append(Slice(img, 0, 120));
        Assert.True(stitcher.IsAtBottom);
        Assert.True(stitcher.LastAppendedHeight <= 2);
        Assert.Equal(120, stitcher.CurrentHeight); // 高度不变
    }

    [Fact]
    public void Stitch_NoOverlapFrame_DoesNotAppendAndFlagsMatchFailure()
    {
        var first = Slice(MakeLongImage(180, seed: 111), 0, 120);
        var unrelated = Slice(MakeLongImage(180, seed: 999), 0, 120);
        var stitcher = new ScrollStitcher(templateHeight: 60);

        stitcher.Append(first);
        stitcher.Append(unrelated);

        Assert.True(stitcher.LastMatchFailed);
        Assert.True(stitcher.LastMatchAverageSadPerChannel > stitcher.MaxAverageSadPerChannel);
        Assert.False(stitcher.IsAtBottom);
        Assert.Equal(0, stitcher.LastAppendedHeight);
        Assert.Equal(120, stitcher.CurrentHeight);
        AssertImagesEqual(first, stitcher.Build());
    }

    [Fact]
    public void Stitch_RepeatedSolidFrame_DetectsBottomInsteadOfAppendingArbitraryRows()
    {
        var frame = MakeSolidFrame(100, new ColorRgba(32, 64, 96, 255));
        var stitcher = new ScrollStitcher(templateHeight: 80);

        stitcher.Append(frame);
        stitcher.Append(frame);

        Assert.False(stitcher.LastMatchFailed);
        Assert.True(stitcher.IsAtBottom);
        Assert.Equal(0, stitcher.LastAppendedHeight);
        Assert.Equal(100, stitcher.CurrentHeight);
        AssertImagesEqual(frame, stitcher.Build());
    }

    [Fact]
    public void Stitch_WithFixedHeader_ComputesCorrectOffset()
    {
        // 模拟"顶部固定 header + 可滚动正文"：
        // 每帧 = [固定 header headerH 行] ++ [长图正文窗口 bodyH 行]
        const int headerH = 30;
        const int bodyH = 120;
        const int totalBody = 360;
        const int step = 60;

        var body = MakeLongImage(totalBody, seed: 999);
        var header = MakeHeader(headerH);

        // 拼装期望长图：header 一次 + 整段正文
        var expected = new PixelBuffer(Width, headerH + totalBody);
        expected.BlitRows(header, 0, 0, headerH);
        expected.BlitRows(body, 0, headerH, totalBody);

        var stitcher = new ScrollStitcher(templateHeight: 80);

        int bodyStart = 0;
        int lastBodyStart = totalBody - bodyH;
        while (true)
        {
            int bs = Math.Min(bodyStart, lastBodyStart);
            stitcher.Append(MakeHeaderedFrame(header, body, headerH, bs, bodyH));
            if (bs >= lastBodyStart) break;
            bodyStart += step;
        }

        var result = stitcher.Build();
        // header 只应出现一次：总高 = headerH + totalBody
        Assert.Equal(headerH + totalBody, result.Height);
        AssertImagesEqual(expected, result);
    }

    private static PixelBuffer MakeHeader(int h)
    {
        // 固定的 header 图案（与正文哈希族不同，且每行也是结构化的）
        var buf = new PixelBuffer(Width, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < Width; x++)
                buf.SetPixel(x, y, new ColorRgba((byte)(200 - y), (byte)(x * 3), (byte)(y * 5 + x), 255));
        return buf;
    }

    private static PixelBuffer MakeSolidFrame(int h, ColorRgba color)
    {
        var buf = new PixelBuffer(Width, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < Width; x++)
                buf.SetPixel(x, y, color);
        return buf;
    }

    private static PixelBuffer MakeHeaderedFrame(PixelBuffer header, PixelBuffer body, int headerH, int bodyStart, int bodyH)
    {
        var f = new PixelBuffer(Width, headerH + bodyH);
        f.BlitRows(header, 0, 0, headerH);
        f.BlitRows(body, bodyStart, headerH, bodyH);
        return f;
    }
}
