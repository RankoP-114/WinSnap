using System.IO;
using Serilog;
using WinSnap.App.Imaging;

namespace WinSnap.App.Diagnostics;

internal static class TempFileCleaner
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);
    private static DateTime _lastCleanupUtc = DateTime.MinValue;

    public static string BuildTempPath(string format)
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinSnap");
        Directory.CreateDirectory(dir);
        CleanupOldFiles(dir, ref _lastCleanupUtc);
        return Path.Combine(dir, ImageSaver.BuildDefaultFileName(format));
    }

    private static void CleanupOldFiles(string dir, ref DateTime lastCleanupUtc)
    {
        var now = DateTime.UtcNow;
        if (now - lastCleanupUtc < TimeSpan.FromMinutes(10))
            return;

        lastCleanupUtc = now;
        foreach (string pattern in new[] { "Screenshot_*.png", "Screenshot_*.gif", "WinSnap_*.png", "WinSnap_*.gif", "WinSnap_pin_*.png" })
        {
            foreach (string path in Directory.EnumerateFiles(dir, pattern))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < now - Retention)
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "清理临时文件失败：{Path}", path);
                }
            }
        }
    }
}
