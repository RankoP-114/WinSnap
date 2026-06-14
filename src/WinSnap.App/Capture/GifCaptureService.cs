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

        int frameCount = Math.Max(1, options.DurationSeconds * options.FramesPerSecond);
        int delayHundredths = Math.Max(1, (int)Math.Round(100.0 / options.FramesPerSecond));
        var interval = TimeSpan.FromSeconds(1.0 / options.FramesPerSecond);
        bool useHdrPath = HdrDetector.AnyHdrActive();
        var stopwatch = Stopwatch.StartNew();

        Log.Information(
            "GIF 录制开始：区域=({X},{Y},{W},{H}) 时长={Duration}s FPS={Fps} HDR路径={Hdr}",
            x, y, width, height, options.DurationSeconds, options.FramesPerSecond, useHdrPath);

        using var encoder = new StreamingGifEncoder(outputPath, width, height, delayHundredths);
        int lastRemainingSeconds = options.DurationSeconds;
        remainingSecondsProgress?.Report(lastRemainingSeconds);
        for (int i = 0; i < frameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = CaptureFrame(x, y, width, height, useHdrPath);
            encoder.WriteFrame(captured);

            int remainingSeconds = Math.Max(0, options.DurationSeconds - ((i + 1) / options.FramesPerSecond));
            if (remainingSeconds != lastRemainingSeconds)
            {
                lastRemainingSeconds = remainingSeconds;
                remainingSecondsProgress?.Report(remainingSeconds);
            }

            var nextFrameAt = TimeSpan.FromTicks(interval.Ticks * (i + 1));
            var wait = nextFrameAt - stopwatch.Elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        Log.Information("GIF 录制完成：{Path}，帧数={Frames}", outputPath, frameCount);
    }

    private static CapturedImage CaptureFrame(int x, int y, int width, int height, bool useHdrPath)
    {
        if (!useHdrPath)
            return GdiCapture.CaptureRegion(x, y, width, height);

        var vs = VirtualScreenInfo.Get();
        var full = DuplicationCapture.CaptureVirtualScreenHdrAware();
        return Crop(full, x - vs.X, y - vs.Y, width, height);
    }

    private static CapturedImage Crop(CapturedImage source, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
            x + width > source.Width || y + height > source.Height)
        {
            throw new InvalidOperationException("GIF 录制区域已超出当前虚拟桌面范围。");
        }

        int rowBytes = width * 4;
        var buffer = new byte[rowBytes * height];
        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                source.PixelsBgra,
                (y + row) * source.Stride + x * 4,
                buffer,
                row * rowBytes,
                rowBytes);
        }
        return new CapturedImage(width, height, buffer);
    }

    private sealed class StreamingGifEncoder : IDisposable
    {
        private const int PaletteSize = 256;
        private readonly FileStream _stream;
        private readonly int _width;
        private readonly int _height;
        private readonly ushort _delayHundredths;
        private bool _disposed;

        public StreamingGifEncoder(string path, int width, int height, int delayHundredths)
        {
            _width = width;
            _height = height;
            _delayHundredths = (ushort)Math.Clamp(delayHundredths, 1, ushort.MaxValue);
            _stream = File.Create(path);
            WriteHeader();
        }

        public void WriteFrame(CapturedImage image)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(StreamingGifEncoder));
            if (image.Width != _width || image.Height != _height)
                throw new InvalidOperationException("GIF 帧尺寸不一致。");

            WriteGraphicControlExtension();
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

        private void WriteGraphicControlExtension()
        {
            _stream.WriteByte(0x21);
            _stream.WriteByte(0xF9);
            _stream.WriteByte(0x04);
            _stream.WriteByte(0x08); // disposal = restore to background
            WriteUInt16(_delayHundredths);
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
