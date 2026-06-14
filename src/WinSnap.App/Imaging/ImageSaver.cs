using System.IO;
using System.Windows.Media.Imaging;

namespace WinSnap.App.Imaging;

/// <summary>把 WPF 位图编码保存为 PNG / JPEG。</summary>
public static class ImageSaver
{
    public static void Save(BitmapSource image, string path, int jpegQuality = 90)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>默认文件名，如 WinSnap_20260604_141530.png。</summary>
    public static string BuildDefaultFileName(string format)
        => $"WinSnap_{DateTime.Now:yyyyMMdd_HHmmss}.{format}";
}
