namespace WinSnap.Core.Settings;

/// <summary>
/// 应用全部可持久化配置。序列化到 %AppData%\WinSnap\settings.json。
/// 字段随里程碑扩展，新增字段务必给默认值以兼容旧配置文件。
/// </summary>
public sealed class AppSettings
{
    // ---- 全局热键（M1 注册 / M9 自定义）----
    public string CaptureHotkey { get; set; } = "Ctrl+Alt+A";
    public string PinHotkey { get; set; } = "Ctrl+Alt+P";
    public string ScrollCaptureHotkey { get; set; } = "Ctrl+Alt+S";

    // ---- 保存（M3）----
    public string DefaultSaveFormat { get; set; } = "png"; // png | jpg
    public int JpegQuality { get; set; } = 90;
    public string? LastSaveDirectory { get; set; }
    public bool QuickSaveWithoutDialog { get; set; }

    // ---- GIF 录制 ----
    public int GifDefaultDurationSeconds { get; set; } = 5;
    public int GifCountdownSeconds { get; set; } = 3;
    public int GifFramesPerSecond { get; set; } = 10;

    // ---- 行为 ----
    public bool StartMinimizedToTray { get; set; } = true;
    public bool RunAtStartup { get; set; }
    public bool ShowMagnifier { get; set; } = true;

    // ---- HDR 色调映射（M4，对标 OBS 的 SDR White / HDR Peak）----
    public double HdrSdrWhiteLevelNits { get; set; } = 420.0;
    public double HdrPeakNits { get; set; } = 1000.0;

    // ---- 界面语言 ----
    public string Language { get; set; } = "zh-CN";

    // ---- 主题（M9，WinUI3 Fluent ThemeMode）----
    public string Theme { get; set; } = "system"; // system | light | dark
}
