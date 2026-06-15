using System.Diagnostics;
using System.IO;
using Serilog;
using WinSnap.Interop;

namespace WinSnap.App.Capture;

public sealed class GifCaptureService
{
    public async Task CaptureAsync(
        int x,
        int y,
        int width,
        int height,
        GifCaptureOptions options,
        string outputPath,
        CancellationToken cancellationToken,
        IProgress<int>? remainingSecondsProgress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "GIF 录制区域必须大于 0。");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("GIF 输出路径不能为空。", nameof(outputPath));

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var duration = TimeSpan.FromSeconds(options.DurationSeconds);
        var interval = TimeSpan.FromSeconds(1.0 / options.FramesPerSecond);
        bool hdrActive = HdrDetector.AnyHdrActive();
        bool foregroundFullscreen = IsForegroundFullscreenLike();
        bool allowDuplicationFallback = hdrActive || foregroundFullscreen;
        bool preferDuplication = hdrActive;
        var stopwatch = Stopwatch.StartNew();

        Log.Information(
            "GIF 录制开始：区域=({X},{Y},{W},{H}) 时长={Duration}s FPS={Fps} HDR={Hdr} 前台全屏={Fullscreen} SDR白={SdrWhite}nit HDR峰值={HdrPeak}nit",
            x, y, width, height, options.DurationSeconds, options.FramesPerSecond,
            hdrActive, foregroundFullscreen, options.HdrSdrWhiteLevelNits, options.HdrPeakNits);

        using var encoder = new StreamingGifEncoder(outputPath, width, height);
        int lastRemainingSeconds = options.DurationSeconds;
        remainingSecondsProgress?.Report(lastRemainingSeconds);
        bool? useDuplication = null;
        int frameCount = 0;
        using var duplicationSession = allowDuplicationFallback
            ? DuplicationCapture.CreateRegionCaptureSession(
                x,
                y,
                width,
                height,
                captureSdrWithDesktopDuplication: true,
                hdrSdrWhiteNits: options.HdrSdrWhiteLevelNits,
                hdrPeakNits: options.HdrPeakNits)
            : null;

        CapturedImage previous = CaptureFrame(
            x, y, width, height, options, duplicationSession, preferDuplication, allowDuplicationFallback, ref useDuplication);
        TimeSpan previousAt = TimeSpan.Zero;
        TimeSpan nextFrameAt = interval;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int remainingSeconds = CalculateRemainingSeconds(duration, stopwatch.Elapsed);
            if (remainingSeconds != lastRemainingSeconds)
            {
                lastRemainingSeconds = remainingSeconds;
                remainingSecondsProgress?.Report(remainingSeconds);
            }

            if (stopwatch.Elapsed >= duration)
                break;

            var wait = nextFrameAt - stopwatch.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                TimeSpan remaining = duration - stopwatch.Elapsed;
                await Task.Delay(wait < remaining ? wait : remaining, cancellationToken).ConfigureAwait(false);
                if (stopwatch.Elapsed >= duration)
                    break;
            }

            var captured = CaptureFrame(
                x, y, width, height, options, duplicationSession, preferDuplication, allowDuplicationFallback, ref useDuplication);
            TimeSpan capturedAt = stopwatch.Elapsed;
            encoder.WriteFrame(previous, ToDelayHundredths(capturedAt - previousAt, interval));
            frameCount++;

            previous = captured;
            previousAt = capturedAt;
            do
            {
                nextFrameAt += interval;
            }
            while (nextFrameAt <= stopwatch.Elapsed && stopwatch.Elapsed < duration);
        }

        encoder.WriteFrame(previous, ToDelayHundredths(duration - previousAt, interval));
        frameCount++;
        remainingSecondsProgress?.Report(0);

        Log.Information("GIF 录制完成：{Path}，帧数={Frames}", outputPath, frameCount);
    }

    private static int CalculateRemainingSeconds(TimeSpan duration, TimeSpan elapsed)
        => Math.Max(0, (int)Math.Ceiling((duration - elapsed).TotalSeconds));

    private static int ToDelayHundredths(TimeSpan actualDelay, TimeSpan fallbackDelay)
    {
        TimeSpan delay = actualDelay > TimeSpan.Zero ? actualDelay : fallbackDelay;
        return Math.Clamp((int)Math.Round(delay.TotalMilliseconds / 10.0), 1, ushort.MaxValue);
    }

    private static CapturedImage CaptureFrame(
        int x,
        int y,
        int width,
        int height,
        GifCaptureOptions options,
        DuplicationCapture.RegionCaptureSession? duplicationSession,
        bool preferDuplication,
        bool allowDuplicationFallback,
        ref bool? useDuplication)
    {
        if (useDuplication == true || preferDuplication)
        {
            try
            {
                var duplicated = CaptureFrameWithDuplication(x, y, width, height, options, duplicationSession);
                if (preferDuplication || duplicated.HasVisibleContent())
                {
                    useDuplication = true;
                    return duplicated;
                }

                Log.Debug("GIF Desktop Duplication 帧看起来全黑，尝试 GDI 路径。");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "GIF Desktop Duplication 抓取失败，尝试 GDI 路径。");
            }
        }

        CapturedImage gdi;
        try
        {
            gdi = GdiCapture.CaptureRegion(x, y, width, height);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GIF GDI 区域抓取失败，尝试 Desktop Duplication 兜底。");
            try
            {
                var duplicated = CaptureFrameWithDuplication(x, y, width, height, options, duplicationSession);
                useDuplication = true;
                Log.Information("GIF 录制已从 GDI 切换到 Desktop Duplication 兜底。");
                return duplicated;
            }
            catch (Exception fallbackEx)
            {
                throw new InvalidOperationException(
                    $"GIF 区域抓取失败：GDI={ex.Message}；Desktop Duplication={fallbackEx.Message}",
                    fallbackEx);
            }
        }

        if (useDuplication == false || !allowDuplicationFallback || gdi.HasVisibleContent())
        {
            useDuplication ??= false;
            return gdi;
        }

        try
        {
            var duplicated = CaptureFrameWithDuplication(x, y, width, height, options, duplicationSession);
            if (duplicated.HasVisibleContent())
            {
                useDuplication = true;
                Log.Information("GIF 录制区域 GDI 首帧看起来全黑，已切换到 Desktop Duplication 兜底。");
                return duplicated;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GIF Desktop Duplication 兜底失败，继续使用 GDI 帧。");
        }

        useDuplication = false;
        return gdi;
    }

    private static CapturedImage CaptureFrameWithDuplication(
        int x,
        int y,
        int width,
        int height,
        GifCaptureOptions options,
        DuplicationCapture.RegionCaptureSession? duplicationSession)
        => duplicationSession?.Capture()
           ?? DuplicationCapture.CaptureRegionHdrAware(
               x,
               y,
               width,
               height,
               captureSdrWithDesktopDuplication: true,
               hdrSdrWhiteNits: options.HdrSdrWhiteLevelNits,
               hdrPeakNits: options.HdrPeakNits);

    private static bool IsForegroundFullscreenLike()
    {
        if (!WindowEnumerator.TryGetForegroundWindowBounds(out var foreground))
            return false;

        var monitors = MonitorEnumerator.GetMonitors();
        if (monitors.Count == 0)
        {
            var vs = VirtualScreenInfo.Get();
            monitors = new List<MonitorInfo> { new(vs.X, vs.Y, vs.Width, vs.Height, 96) };
        }

        foreach (var monitor in monitors)
        {
            if (Covers(foreground, monitor, tolerancePx: 8))
                return true;
        }

        return false;
    }

    private static bool Covers(WindowEnumerator.WindowBounds window, MonitorInfo monitor, int tolerancePx)
    {
        int windowRight = window.X + window.Width;
        int windowBottom = window.Y + window.Height;

        return window.X <= monitor.X + tolerancePx
               && window.Y <= monitor.Y + tolerancePx
               && windowRight >= monitor.Right - tolerancePx
               && windowBottom >= monitor.Bottom - tolerancePx
               && window.Width >= monitor.Width - tolerancePx
               && window.Height >= monitor.Height - tolerancePx;
    }

    private sealed class StreamingGifEncoder : IDisposable
    {
        private const int PaletteSize = 256;
        private readonly FileStream _stream;
        private readonly int _width;
        private readonly int _height;
        private bool _disposed;

        public StreamingGifEncoder(string path, int width, int height)
        {
            _width = width;
            _height = height;
            _stream = File.Create(path);
            WriteHeader();
        }

        public void WriteFrame(CapturedImage image, int delayHundredths)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(StreamingGifEncoder));
            if (image.Width != _width || image.Height != _height)
                throw new InvalidOperationException("GIF 帧尺寸不一致。");

            WriteGraphicControlExtension((ushort)Math.Clamp(delayHundredths, 1, ushort.MaxValue));
            WriteImageDescriptor();
            WriteLzwImageData(image);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _stream.WriteByte(0x3B); // trailer
            _stream.Dispose();
            _disposed = true;
        }

        private void WriteHeader()
        {
            WriteAscii("GIF89a");
            WriteUInt16((ushort)_width);
            WriteUInt16((ushort)_height);
            _stream.WriteByte(0xF7); // global color table, 8 bits per primary, 256 colors
            _stream.WriteByte(0x00); // background color index
            _stream.WriteByte(0x00); // pixel aspect ratio
            WriteGlobalPalette();
            WriteLoopExtension();
        }

        private void WriteGlobalPalette()
        {
            for (int i = 0; i < PaletteSize; i++)
            {
                int r = ((i >> 5) & 0x07) * 255 / 7;
                int g = ((i >> 2) & 0x07) * 255 / 7;
                int b = (i & 0x03) * 255 / 3;
                _stream.WriteByte((byte)r);
                _stream.WriteByte((byte)g);
                _stream.WriteByte((byte)b);
            }
        }

        private void WriteLoopExtension()
        {
            _stream.WriteByte(0x21);
            _stream.WriteByte(0xFF);
            _stream.WriteByte(0x0B);
            WriteAscii("NETSCAPE2.0");
            _stream.WriteByte(0x03);
            _stream.WriteByte(0x01);
            WriteUInt16(0); // loop forever
            _stream.WriteByte(0x00);
        }

        private void WriteGraphicControlExtension(ushort delayHundredths)
        {
            _stream.WriteByte(0x21);
            _stream.WriteByte(0xF9);
            _stream.WriteByte(0x04);
            _stream.WriteByte(0x00); // no transparency, leave previous full frame in place until next frame
            WriteUInt16(delayHundredths);
            _stream.WriteByte(0x00);
            _stream.WriteByte(0x00);
        }

        private void WriteImageDescriptor()
        {
            _stream.WriteByte(0x2C);
            WriteUInt16(0);
            WriteUInt16(0);
            WriteUInt16((ushort)_width);
            WriteUInt16((ushort)_height);
            _stream.WriteByte(0x00);
        }

        private void WriteLzwImageData(CapturedImage image)
        {
            const int minCodeSize = 8;
            const int clearCode = 1 << minCodeSize;
            const int endCode = clearCode + 1;
            int nextCode = endCode + 1;
            int codeSize = minCodeSize + 1;
            var dictionary = new Dictionary<int, int>(4096);

            _stream.WriteByte(minCodeSize);
            using var writer = new GifSubBlockBitWriter(_stream);
            writer.WriteCode(clearCode, codeSize);

            int pixelCount = image.Width * image.Height;
            if (pixelCount == 0)
            {
                writer.WriteCode(endCode, codeSize);
                return;
            }

            int prefix = Quantize(image.PixelsBgra, 0);
            for (int p = 1; p < pixelCount; p++)
            {
                int next = Quantize(image.PixelsBgra, p * 4);
                int key = (prefix << 8) | next;
                if (dictionary.TryGetValue(key, out int combined))
                {
                    prefix = combined;
                    continue;
                }

                writer.WriteCode(prefix, codeSize);
                if (nextCode < 4096)
                {
                    dictionary[key] = nextCode++;
                    if (nextCode == (1 << codeSize) && codeSize < 12)
                        codeSize++;
                }
                else
                {
                    writer.WriteCode(clearCode, codeSize);
                    dictionary.Clear();
                    codeSize = minCodeSize + 1;
                    nextCode = endCode + 1;
                }
                prefix = next;
            }

            writer.WriteCode(prefix, codeSize);
            writer.WriteCode(endCode, codeSize);
        }

        private static int Quantize(byte[] bgra, int offset)
        {
            int b = bgra[offset];
            int g = bgra[offset + 1];
            int r = bgra[offset + 2];
            int ri = r * 7 / 255;
            int gi = g * 7 / 255;
            int bi = b * 3 / 255;
            return (ri << 5) | (gi << 2) | bi;
        }

        private void WriteUInt16(ushort value)
        {
            _stream.WriteByte((byte)(value & 0xFF));
            _stream.WriteByte((byte)(value >> 8));
        }

        private void WriteAscii(string text)
        {
            foreach (char ch in text)
                _stream.WriteByte((byte)ch);
        }
    }

    private sealed class GifSubBlockBitWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _block = new byte[255];
        private int _blockLength;
        private int _bitBuffer;
        private int _bitCount;
        private bool _disposed;

        public GifSubBlockBitWriter(Stream stream)
        {
            _stream = stream;
        }

        public void WriteCode(int code, int codeSize)
        {
            _bitBuffer |= code << _bitCount;
            _bitCount += codeSize;
            while (_bitCount >= 8)
            {
                WriteByte((byte)(_bitBuffer & 0xFF));
                _bitBuffer >>= 8;
                _bitCount -= 8;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            if (_bitCount > 0)
                WriteByte((byte)(_bitBuffer & 0xFF));
            FlushBlock();
            _stream.WriteByte(0x00);
            _disposed = true;
        }

        private void WriteByte(byte value)
        {
            _block[_blockLength++] = value;
            if (_blockLength == _block.Length)
                FlushBlock();
        }

        private void FlushBlock()
        {
            if (_blockLength == 0)
                return;
            _stream.WriteByte((byte)_blockLength);
            _stream.Write(_block, 0, _blockLength);
            _blockLength = 0;
        }
    }
}
