using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
public sealed class ScrollStitcher : IDisposable
{
    private static readonly Vector128<byte> BgrMask128 = Vector128.Create(0x00FFFFFFu).AsByte();
    private static readonly Vector256<byte> BgrMask256 = Vector256.Create(0x00FFFFFFu).AsByte();

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
    private bool _bgraFromPool;
    private int _width;
    private int _height;        // 当前结果高度
    private int _capacityHeight;
    private byte[]? _lastFrame; // 上一帧像素
    private bool _lastFrameFromPool;
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

    /// <summary>
    /// 追加一帧。首帧作基底；其后按重叠对齐追加新增部分。
    /// </summary>
    /// <param name="frame">要追加的滚动帧。</param>
    /// <param name="ownsFrame">
    /// 当调用方保证追加后不再修改或复用 <paramref name="frame"/> 的像素数组时传 true，
    /// 拼接器可直接保留该数组作为上一帧，避免一次大图克隆。
    /// </param>
    public void Append(PixelBuffer frame, bool ownsFrame = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException("帧尺寸必须为正。", nameof(frame));

        if (_bgra is null)
        {
            // 首帧：整幅作为基底
            _width = frame.Width;
            _height = frame.Height;
            _bgra = RetainFrame(frame, ownsFrame, out _bgraFromPool);
            _capacityHeight = Math.Max(frame.Height, _bgra.Length / frame.Stride);
            _lastFrame = _bgra;
            _lastFrameFromPool = _bgraFromPool;
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
            ReplaceLastFrame(frame, ownsFrame);
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
            if (newRows > 0)
                AppendBottomRows(frame, frame.Height - newRows, newRows);
            ReplaceLastFrame(frame, ownsFrame);
            return;
        }

        AppendBottomRows(frame, frame.Height - newRows, newRows);
        LastAppendedHeight = newRows;
        IsAtBottom = false;
        ReplaceLastFrame(frame, ownsFrame);
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

    /// <summary>构建当前拼接结果并立即释放内部缓冲。适用于一次性长截图导出，降低最终转换阶段峰值内存。</summary>
    public PixelBuffer BuildAndReset()
    {
        var result = Build();
        Reset();
        return result;
    }

    /// <summary>
    /// 把当前内部 BGRA 缓冲同步交给调用方创建导出对象，然后立即释放内部缓冲。
    /// 调用方必须在回调返回前完成拷贝，不得保存传入数组引用。
    /// </summary>
    public TResult ExportAndReset<TResult>(Func<int, int, byte[], int, TResult> createFromBgra)
    {
        ArgumentNullException.ThrowIfNull(createFromBgra);

        if (_bgra is null || _width == 0 || _height == 0)
        {
            Reset();
            return createFromBgra(0, 0, Array.Empty<byte>(), 0);
        }

        int width = _width;
        int height = _height;
        int stride = checked(width * PixelBuffer.BytesPerPixel);
        byte[] bgra = _bgra;

        try
        {
            return createFromBgra(width, height, bgra, stride);
        }
        finally
        {
            Reset();
        }
    }

    /// <summary>重置到初始状态，可复用本实例开始新一次拼接。</summary>
    public void Reset()
    {
        ReturnInternalBuffers();
        _bgra = null;
        _bgraFromPool = false;
        _lastFrame = null;
        _lastFrameFromPool = false;
        _width = 0;
        _height = 0;
        _capacityHeight = 0;
        _lastFrameHeight = 0;
        FrameCount = 0;
        LastAppendedHeight = 0;
        LastMatchFailed = false;
        LastMatchAverageSadPerChannel = 0.0;
        IsAtBottom = false;
    }

    public void Dispose() => Reset();

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

        long bestSad;
        int bestRow;

        if (TryFindExactSampleMatch(last, tStart, frame.Bgra, h, stride, searchMax, out bestRow))
        {
            bestSad = 0;
        }
        else
        {
            bestSad = long.MaxValue;
            bestRow = 0;
            SeedBestMatchFromCoarsePass(last, tStart, frame.Bgra, h, stride, searchMax, ref bestSad, ref bestRow);

            for (int m = 0; m <= searchMax; m++)
            {
                if (m == bestRow)
                    continue;

                long sad = ComputeSad(last, tStart, frame.Bgra, m, h, stride, bestSad);
                if (sad < bestSad || (sad == bestSad && m > bestRow))
                {
                    bestSad = sad;
                    bestRow = m;
                }
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
    /// 使用更稀疏的行/列采样粗评估全部候选行，再对粗匹配行做一次完整 SAD。
    /// 后续完整搜索仍会遍历所有候选，所以该步骤只影响速度，不改变匹配结果。
    /// </summary>
    private void SeedBestMatchFromCoarsePass(
        byte[] last,
        int tStart,
        byte[] cand,
        int h,
        int stride,
        int searchMax,
        ref long bestSad,
        ref int bestRow)
    {
        if (searchMax < 16)
            return;

        long bestCoarseSad = long.MaxValue;
        int bestCoarseRow = 0;
        int coarseRowStep = Math.Max(RowSampleStep * 4, RowSampleStep);
        int coarseColumnStepBytes = checked(ColumnSampleStep * PixelBuffer.BytesPerPixel * 4);

        for (int m = searchMax; m >= 0; m--)
        {
            long coarseSad = ComputeCoarseSad(
                last,
                tStart,
                cand,
                m,
                h,
                stride,
                coarseRowStep,
                coarseColumnStepBytes,
                bestCoarseSad);
            if (coarseSad < bestCoarseSad)
            {
                bestCoarseSad = coarseSad;
                bestCoarseRow = m;
            }
        }

        bestSad = ComputeSad(last, tStart, cand, bestCoarseRow, h, stride, currentBest: long.MaxValue);
        bestRow = bestCoarseRow;

        for (int delta = 1; delta <= 2; delta++)
        {
            int before = bestCoarseRow - delta;
            if (before >= 0)
            {
                long sad = ComputeSad(last, tStart, cand, before, h, stride, bestSad);
                if (sad < bestSad || (sad == bestSad && before > bestRow))
                {
                    bestSad = sad;
                    bestRow = before;
                }
            }

            int after = bestCoarseRow + delta;
            if (after <= searchMax)
            {
                long sad = ComputeSad(last, tStart, cand, after, h, stride, bestSad);
                if (sad < bestSad || (sad == bestSad && after > bestRow))
                {
                    bestSad = sad;
                    bestRow = after;
                }
            }
        }
    }

    private long ComputeCoarseSad(
        byte[] last,
        int tStart,
        byte[] cand,
        int candStart,
        int h,
        int stride,
        int rowStep,
        int colStepBytes,
        long currentBest)
    {
        long sad = 0;
        for (int row = 0; row < h; row += rowStep)
        {
            int lOff = (tStart + row) * stride;
            int cOff = (candStart + row) * stride;
            for (int xb = 0; xb < stride; xb += colStepBytes)
            {
                int li = lOff + xb;
                int ci = cOff + xb;
                sad += Math.Abs(last[li] - cand[ci]);
                sad += Math.Abs(last[li + 1] - cand[ci + 1]);
                sad += Math.Abs(last[li + 2] - cand[ci + 2]);
                if (sad > currentBest)
                    return long.MaxValue;
            }
        }

        return sad;
    }

    private bool TryFindExactSampleMatch(
        byte[] last,
        int tStart,
        byte[] cand,
        int h,
        int stride,
        int searchMax,
        out int bestRow)
    {
        bestRow = 0;
        int sampledRows = ((h - 1) / RowSampleStep) + 1;
        int[]? templateRows = null;
        ulong[]? templateHashes = null;
        ulong[]? candidateHashes = null;

        try
        {
            templateRows = ArrayPool<int>.Shared.Rent(sampledRows);
            templateHashes = ArrayPool<ulong>.Shared.Rent(sampledRows);
            int templateCount = 0;
            for (int row = 0; row < h; row += RowSampleStep)
            {
                templateRows[templateCount] = row;
                templateHashes[templateCount] = ComputeSampledRowHash(last, tStart + row, stride);
                templateCount++;
            }

            int candidateHeight = searchMax + h;
            candidateHashes = ArrayPool<ulong>.Shared.Rent(candidateHeight);
            for (int row = 0; row < candidateHeight; row++)
            {
                candidateHashes[row] = ComputeSampledRowHash(cand, row, stride);
            }

            for (int m = searchMax; m >= 0; m--)
            {
                bool possible = true;
                for (int i = 0; i < templateCount; i++)
                {
                    if (candidateHashes[m + templateRows[i]] != templateHashes[i])
                    {
                        possible = false;
                        break;
                    }
                }

                if (!possible)
                    continue;

                if (ComputeSad(last, tStart, cand, m, h, stride, currentBest: 0) == 0)
                {
                    bestRow = m;
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (templateRows is not null) ArrayPool<int>.Shared.Return(templateRows);
            if (templateHashes is not null) ArrayPool<ulong>.Shared.Return(templateHashes);
            if (candidateHashes is not null) ArrayPool<ulong>.Shared.Return(candidateHashes);
        }
    }

    private ulong ComputeSampledRowHash(byte[] pixels, int row, int stride)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        int rowOffset = row * stride;
        int colStepBytes = ColumnSampleStep * PixelBuffer.BytesPerPixel;

        for (int xb = 0; xb < stride; xb += colStepBytes)
        {
            int i = rowOffset + xb;
            hash = (hash ^ pixels[i]) * prime;
            hash = (hash ^ pixels[i + 1]) * prime;
            hash = (hash ^ pixels[i + 2]) * prime;
        }

        return hash;
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
        if (ColumnSampleStep == 1 && (Avx2.IsSupported || Sse2.IsSupported))
            return ComputeSadContiguousBgrSimd(last, tStart, cand, candStart, h, stride, currentBest);

        const int EarlyAbandonCheckInterval = 32;
        long sad = 0;
        int bpp = PixelBuffer.BytesPerPixel;
        int colStepBytes = ColumnSampleStep * bpp;

        for (int row = 0; row < h; row += RowSampleStep)
        {
            int lOff = (tStart + row) * stride;
            int cOff = (candStart + row) * stride;
            int sampledColumns = 0;
            // 列采样：仅比较 R/G/B（跳过 alpha）
            for (int xb = 0; xb < stride; xb += colStepBytes)
            {
                int li = lOff + xb;
                int ci = cOff + xb;
                sad += Math.Abs(last[li] - cand[ci]);         // B
                sad += Math.Abs(last[li + 1] - cand[ci + 1]); // G
                sad += Math.Abs(last[li + 2] - cand[ci + 2]); // R
                sampledColumns++;
                if ((sampledColumns % EarlyAbandonCheckInterval) == 0 && sad > currentBest)
                    return long.MaxValue;
            }
            if (sad > currentBest)
                return long.MaxValue; // 提前放弃
        }
        return sad;
    }

    private long ComputeSadContiguousBgrSimd(
        byte[] last,
        int tStart,
        byte[] cand,
        int candStart,
        int h,
        int stride,
        long currentBest)
    {
        long sad = 0;

        for (int row = 0; row < h; row += RowSampleStep)
        {
            int lOff = (tStart + row) * stride;
            int cOff = (candStart + row) * stride;
            sad = ComputeSadRowContiguousBgrSimd(last, lOff, cand, cOff, stride, sad, currentBest);
            if (sad > currentBest)
                return long.MaxValue;
        }

        return sad;
    }

    private static long ComputeSadRowContiguousBgrSimd(
        byte[] last,
        int lOff,
        byte[] cand,
        int cOff,
        int rowBytes,
        long sad,
        long currentBest)
    {
        int xb = 0;

        if (Avx2.IsSupported)
        {
            for (; xb <= rowBytes - Vector256<byte>.Count; xb += Vector256<byte>.Count)
            {
                var left = Avx2.And(MemoryMarshal.Read<Vector256<byte>>(last.AsSpan(lOff + xb)), BgrMask256);
                var right = Avx2.And(MemoryMarshal.Read<Vector256<byte>>(cand.AsSpan(cOff + xb)), BgrMask256);
                var sums = Avx2.SumAbsoluteDifferences(left, right).AsUInt64();
                sad += (long)sums.GetElement(0)
                       + (long)sums.GetElement(1)
                       + (long)sums.GetElement(2)
                       + (long)sums.GetElement(3);
                if (sad > currentBest)
                    return long.MaxValue;
            }
        }

        if (Sse2.IsSupported)
        {
            for (; xb <= rowBytes - Vector128<byte>.Count; xb += Vector128<byte>.Count)
            {
                var left = Sse2.And(MemoryMarshal.Read<Vector128<byte>>(last.AsSpan(lOff + xb)), BgrMask128);
                var right = Sse2.And(MemoryMarshal.Read<Vector128<byte>>(cand.AsSpan(cOff + xb)), BgrMask128);
                var sums = Sse2.SumAbsoluteDifferences(left, right).AsUInt64();
                sad += (long)sums.GetElement(0) + (long)sums.GetElement(1);
                if (sad > currentBest)
                    return long.MaxValue;
            }
        }

        for (; xb < rowBytes; xb += PixelBuffer.BytesPerPixel)
        {
            int li = lOff + xb;
            int ci = cOff + xb;
            sad += Math.Abs(last[li] - cand[ci]);         // B
            sad += Math.Abs(last[li + 1] - cand[ci + 1]); // G
            sad += Math.Abs(last[li + 2] - cand[ci + 2]); // R
            if (sad > currentBest)
                return long.MaxValue;
        }

        return sad;
    }

    /// <summary>把 frame 从 srcStartRow 起的 count 行追加到结果底部。</summary>
    private void AppendBottomRows(PixelBuffer frame, int srcStartRow, int count)
    {
        if (count <= 0) return;
        int stride = _width * PixelBuffer.BytesPerPixel;
        int newHeight = checked(_height + count);
        EnsureCapacity(newHeight, stride);
        Buffer.BlockCopy(frame.Bgra, checked(srcStartRow * stride), _bgra!, checked(_height * stride), checked(count * stride));
        _height = newHeight;
    }

    private void EnsureCapacity(int requiredHeight, int stride)
    {
        if (_bgra is null)
            throw new InvalidOperationException("拼接缓冲尚未初始化。");
        if (requiredHeight <= _capacityHeight)
            return;

        int newCapacity = Math.Max(requiredHeight, checked(Math.Max(_capacityHeight, 1) * 2));
        var previous = _bgra;
        bool previousFromPool = _bgraFromPool;
        var grown = ArrayPool<byte>.Shared.Rent(checked(newCapacity * stride));
        Buffer.BlockCopy(_bgra, 0, grown, 0, checked(_height * stride));
        _bgra = grown;
        _bgraFromPool = true;
        _capacityHeight = Math.Max(newCapacity, grown.Length / stride);

        if (previousFromPool && !ReferenceEquals(_lastFrame, previous))
            ArrayPool<byte>.Shared.Return(previous);
    }

    private void ReplaceLastFrame(PixelBuffer frame, bool ownsFrame)
    {
        var previous = _lastFrame;
        bool previousFromPool = _lastFrameFromPool;

        _lastFrame = RetainFrame(frame, ownsFrame, out _lastFrameFromPool);
        _lastFrameHeight = frame.Height;

        if (previousFromPool && !ReferenceEquals(previous, _bgra))
            ArrayPool<byte>.Shared.Return(previous!);
    }

    private void ReturnInternalBuffers()
    {
        var bgra = _bgra;
        var last = _lastFrame;
        if (_bgraFromPool && bgra is not null)
            ArrayPool<byte>.Shared.Return(bgra);
        if (_lastFrameFromPool && last is not null && !ReferenceEquals(last, bgra))
            ArrayPool<byte>.Shared.Return(last);
    }

    private static byte[] RetainFrame(PixelBuffer frame, bool ownsFrame, out bool fromPool)
    {
        if (ownsFrame)
        {
            fromPool = false;
            return frame.Bgra;
        }

        byte[] copy = ArrayPool<byte>.Shared.Rent(frame.Bgra.Length);
        Buffer.BlockCopy(frame.Bgra, 0, copy, 0, frame.Bgra.Length);
        fromPool = true;
        return copy;
    }
}
