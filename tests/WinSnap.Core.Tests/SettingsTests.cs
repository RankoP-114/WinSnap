using System.Text.Json;
using WinSnap.Core.Settings;

namespace WinSnap.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void AppSettings_DefaultsIncludeGifRecordingOptions()
    {
        var settings = new AppSettings();

        Assert.Equal(5, settings.GifDefaultDurationSeconds);
        Assert.Equal(3, settings.GifCountdownSeconds);
        Assert.Equal(10, settings.GifFramesPerSecond);
        Assert.Equal("Ctrl+Alt+G", settings.GifCaptureHotkey);
        Assert.Equal(string.Empty, settings.ScrollCaptureHotkey);
    }

    [Fact]
    public void AppSettings_DeserializesOldJsonWithGifDefaults()
    {
        const string json = """
            {
              "captureHotkey": "Ctrl+Alt+A",
              "defaultSaveFormat": "png"
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        Assert.NotNull(settings);
        Assert.Equal(5, settings!.GifDefaultDurationSeconds);
        Assert.Equal(3, settings.GifCountdownSeconds);
        Assert.Equal(10, settings.GifFramesPerSecond);
        Assert.Equal("Ctrl+Alt+G", settings.GifCaptureHotkey);
        Assert.Equal(string.Empty, settings.ScrollCaptureHotkey);
    }
}
