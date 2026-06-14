using WinSnap.Core.Settings;

namespace WinSnap.App.Capture;

public sealed record GifCaptureOptions(
    int DurationSeconds,
    int CountdownSeconds,
    int FramesPerSecond)
{
    public static GifCaptureOptions FromSettings(AppSettings settings, int? durationOverrideSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GifCaptureOptions(
            Math.Clamp(durationOverrideSeconds ?? settings.GifDefaultDurationSeconds, 1, 60),
            Math.Clamp(settings.GifCountdownSeconds, 0, 10),
            Math.Clamp(settings.GifFramesPerSecond, 1, 20));
    }
}
