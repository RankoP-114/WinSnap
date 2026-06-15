using WinSnap.Core.Imaging;
using WinSnap.Core.Settings;

namespace WinSnap.App.Capture;

public sealed record GifCaptureOptions(
    int DurationSeconds,
    int CountdownSeconds,
    int FramesPerSecond,
    double HdrSdrWhiteLevelNits = ToneMapper.DefaultSdrWhiteNits,
    double HdrPeakNits = ToneMapper.DefaultHdrPeakNits)
{
    public static GifCaptureOptions FromSettings(AppSettings settings, int? durationOverrideSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double hdrSdrWhiteNits = Math.Clamp(settings.HdrSdrWhiteLevelNits, 80.0, 1000.0);
        return new GifCaptureOptions(
            Math.Clamp(durationOverrideSeconds ?? settings.GifDefaultDurationSeconds, 1, 60),
            Math.Clamp(settings.GifCountdownSeconds, 0, 10),
            Math.Clamp(settings.GifFramesPerSecond, 1, 20),
            hdrSdrWhiteNits,
            Math.Clamp(settings.HdrPeakNits, hdrSdrWhiteNits, 4000.0));
    }
}
