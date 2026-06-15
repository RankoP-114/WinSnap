using WinSnap.Core.Primitives;

namespace WinSnap.Core.Stitching;

/// <summary>
/// 长截图滚动拼接器：把一系列「有竖直重叠、等宽」的滚动帧增量拼成一张长图。
///
/// 算法（纯 C#，无原生依赖）：
/// 1. 首帧整幅作为基底；
/// 2. 每来一帧，取「上一帧底部约 <see cref="TemplateHeight"/> 像素」的窄条带作模板
///    （列方向每隔 <see cref="ColumnSampleStep"/> 采样一列以降复杂度），
///    在新帧中自上而下用 SAD（绝对差和）模板匹配求最佳竖直对齐位置；
/// 3. 由匹配位置推出新帧中「超出上一帧底部」的新增区域，追加到结果底部；
/// 4. 某帧新增高度 ≈ 0（&lt;= <see cref="BottomThreshold"/>）视为已到底。
///
/// 模板取自上一帧底部（正文区），因此页面顶部固定 header（每帧相同的前若干行）
/// 不会污染匹配，竖直位移 dy 仍正确。
/// </summary>
public sealed class ScrollStitcher
{
    /// <summary>模板条带高度（像素）。</summary>
    public int TemplateHeight { get; }

    /// <summary>列采样步长：每隔该列数取一列参与 SAD（≥1）。</summary>
    public int ColumnSampleStep { get; }

    /// <summary>行采样步长：模板内每隔该行数取一行参与 SAD（≥1）。</summary>
    public int RowSampleStep { get; }

    /// <summary>判定"到底"的新增高度阈值（像素）：新增 ≤ 此值即认为到底。</summary>
    public int BottomThreshold { get; }

    /// <summary>匹配可接受的最大平均 SAD（按 RGB 通道归一化，0=完全一致）。</summary>
    public double MaxAverageSadPerChannel { get; }

    private byte[]? _bgra;      // 结果像素（BGRA，top-down，stride=_width*4）
    private int _width;
    private int _height;        // 当前结果高度
    private byte[]? _lastFrame; // 上一帧像素
    private int _lastFrameHeight;

    /// <summary>当前已拼接结果的高度（像素）。</summary>
    public int CurrentHeight => _height;

    /// <summary>当前结果宽度（首帧后确定）。</summary>
    public int Width => _width;

    /// <summary>已追加的帧数。</summary>
    public int FrameCount { get; private set; }

    /// <summary>最近一次 <see cref="Append"/> 实际新增的高度（像素）。首帧为其整高。</summary>
    public int LastAppendedHeight { get; private set; }

    /// <summary>最近一次非首帧追加是否因重叠匹配置信度不足而失败。</summary>
    public bool LastMatchFailed { get; private set; }

    /// <summary>最近一次匹配的平均 SAD（按 RGB 通道归一化）。匹配未执行时为 0。</summary>
    public double LastMatchAverageSadPerChannel { get; private set; }

    /// <summary>是否已检测到滚动到底（最近一帧几乎无新增内容）。</summary>
    public bool IsAtBottom { get; private set; }

    public ScrollStitcher(
        int templateHeight = 120,
        int columnSampleStep = 8,
        int rowSampleStep = 2,
        int bottomThreshold = 2,
        double maxAverageSadPerChannel = 24.0)
    {
        if (templateHeight < 1) throw new ArgumentOutOfRangeException(nameof(templateHeight));
        if (columnSampleStep < 1) throw new ArgumentOutOfRangeException(nameof(columnSampleStep));
        if (rowSampleStep < 1) throw new ArgumentOutOfRangeException(nameof(rowSampleStep));
        if (bottomThreshold < 0) throw new ArgumentOutOfRangeException(nameof(bottomThreshold));
        if (maxAverageSadPerChannel <= 0) throw new ArgumentOutOfRangeException(nameof(maxAverageSadPerChannel));
        TemplateHeight = templateHeight;
        ColumnSampleStep = columnSampleStep;
        RowSampleStep = rowSampleStep;
        BottomThreshold = bottomThreshold;
        MaxAverageSadPerChannel = maxAverageSadPerChannel;
    }

    /// <summary>追加一帧。首帧作基底；其后按重叠对齐追加新增部分。</summary>
    public void Append(PixelBuffer frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException("帧尺寸必须为正。", nameof(frame));

        if (_bgra is null)
        {
            // 首帧：整幅作为基底
            _width = frame.Width;
            _height = frame.Height;
            _bgra = (byte[])frame.Bgra.Clone();
            _lastFrame = (byte[])frame.Bgra.Clone();
            _lastFrameHeight = frame.Height;
            FrameCount = 1;
            LastAppendedHeight = frame.Height;
            LastMatchFailed = false;
            LastMatchAverageSadPerChannel = 0.0;
            IsAtBottom = false;
            return;
        }

        if (frame.Width != _width)
            throw new ArgumentException(
                $"帧宽必须与基底一致（期望 {_width}，实际 {frame.Width}）。", nameof(frame));

        // 求新帧相对上一帧的竖直对齐：模板取自上一帧底部 H 行
        int h = Math.Min(TemplateHeight, Math.Min(_lastFrameHeight, frame.Height));
        FrameCount++;

        if (!TryMatchAndComputeNewRows(frame, h, out int newRows, out _, out double averageSad))
        {
            LastAppendedHeight = 0;
            LastMatchFailed = true;
            LastMatchAverageSadPerChannel = averageSad;
            IsAtBottom = false;
            _lastFrame = (byte[])frame.Bgra.Clone();
            _lastFrameHeight = frame.Height;
            return;
        }

        LastMatchFailed = false;
        LastMatchAverageSadPerChannel = averageSad;

        if (newRows <= BottomThreshold)
        {
            // 到底：几乎无新增内容
            LastAppendedHeight = Math.Max(0, newRows);
            IsAtBottom = true;
            // 仍更新 lastFrame，便于后续（通常不会再追加）
            _lastFrame = (byte[])frame.Bgra.Clone();
            _lastFrameHeight = frame.Height;
            if (newRows > 0)
                AppendBottomRows(frame, frame.Height - newRows, newRows);
            return;
        }

        AppendBottomRows(frame, frame.Height - newRows, newRows);
        LastAppendedHeight = newRows;
        IsAtBottom = false;
        _lastFrame = (byte[])frame.Bgra.Clone();
        _lastFrameHeight = frame.Height;
    }

    /// <summary>构建并返回当前拼接结果的快照（深拷贝）。无任何帧时返回 0x0 缓冲。</summary>
    public PixelBuffer Build()
    {
        if (_bgra is null || _width == 0 || _height == 0)
            return new PixelBuffer(0, 0);
        var copy = new byte[checked(_height * _width * PixelBuffer.BytesPerPixel)];
        Buffer.BlockCopy(_bgra, 0, copy, 0, copy.Length);
        return new PixelBuffer(_width, _height, copy);
    }

    /// <summary>重置到初始状态，可复用本实例开始新一次拼接。</summary>
    public void Reset()
    {
        _bgra = null;
        _lastFrame = null;
        _width = 0;
        _height = 0;
        _lastFrameHeight = 0;
        FrameCount = 0;
        LastAppendedHeight = 0;
        LastMatchFailed = false;
        LastMatchAverageSadPerChannel = 0.0;
        IsAtBottom = false;
    }

    /// <summary>
    /// 在新帧中匹配「上一帧底部 h 行」模板，返回新帧底部需追加的行数。
    /// </summary>
    private bool TryMatchAndComputeNewRows(
        PixelBuffer frame,
        int h,
        out int newRows,
        out int bestMatchRow,
        out double averageSadPerChannel)
    {
        byte[] last = _lastFrame!;
        int stride = _width * PixelBuffer.BytesPerPixel;

        int tStart = _lastFrameHeight - h;          // 模板在上一帧中的起始行
        int searchMax = frame.Height - h;            // 新帧中模板可能的最大起始行
        if (searchMax < 0) searchMax = 0;

        long bestSad = long.MaxValue;
        int bestRow = 0;

        // 预先收集采样列偏移
        for (int m = 0; m <= searchMax; m++)
        {
            long sad = ComputeSad(last, tStart, frame.Bgra, m, h, stride, bestSad);
            if (sad < bestSad || (sad == bestSad && m > bestRow))
            {
                bestSad = sad;
                bestRow = m;
            }
        }

        bestMatchRow = bestRow;
        int sampledRows = ((h - 1) / RowSampleStep) + 1;
        int sampledCols = ((_width - 1) / ColumnSampleStep) + 1;
        long sampledChannels = (long)sampledRows * sampledCols * 3;
        averageSadPerChannel = sampledChannels > 0
            ? bestSad / (double)sampledChannels
            : double.PositiveInfinity;

        // 新帧行 (bestRow + h) 起为「超出上一帧底部」的新内容
        int firstNewRow = bestRow + h;
        newRows = frame.Height - firstNewRow;
        if (newRows < 0) newRows = 0;
        return averageSadPerChannel <= MaxAverageSadPerChannel;
    }

    /// <summary>
    /// 计算模板（last 从 tStart 起 h 行）与候选（cand 从 candStart 起 h 行）的采样 SAD。
    /// 带列方向 early-abandon：累计超过 <paramref name="currentBest"/> 立即返回。
    /// </summary>
    private long ComputeSad(
        byte[] last, int tStart,
        byte[] cand, int candStart,
        int h, int stride, long currentBest)
    {
        long sad = 0;
        int bpp = PixelBuffer.BytesPerPixel;
        int colStepBytes = ColumnSampleStep * bpp;

        for (int row = 0; row < h; row += RowSampleStep)
        {
            int lOff = (tStart + row) * stride;
            int cOff = (candStart + row) * stride;
            // 列采样：仅比较 R/G/B（跳过 alpha）
            for (int xb = 0; xb < stride; xb += colStepBytes)
            {
                int li = lOff + xb;
                int ci = cOff + xb;
                sad += Math.Abs(last[li] - cand[ci]);         // B
                sad += Math.Abs(last[li + 1] - cand[ci + 1]); // G
                sad += Math.Abs(last[li + 2] - cand[ci + 2]); // R
            }
            if (sad > currentBest)
                return long.MaxValue; // 提前放弃
        }
        return sad;
    }

    /// <summary>把 frame 从 srcStartRow 起的 count 行追加到结果底部。</summary>
    private void AppendBottomRows(PixelBuffer frame, int srcStartRow, int count)
    {
        if (count <= 0) return;
        int stride = _width * PixelBuffer.BytesPerPixel;
        int newHeight = checked(_height + count);
        var grown = new byte[checked(newHeight * stride)];
        Buffer.BlockCopy(_bgra!, 0, grown, 0, checked(_height * stride));
        Buffer.BlockCopy(frame.Bgra, checked(srcStartRow * stride), grown, checked(_height * stride), checked(count * stride));
        _bgra = grown;
        _height = newHeight;
    }
}
