using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using WinSnap.App.Theming;
using WinSnap.Core.Settings;
using WinSnap.Interop;

namespace WinSnap.App.Settings;

/// <summary>
/// WinUI3 Fluent 风格的设置窗口。分区：主题、截图热键、开机自启、保存格式/目录、放大镜。
/// <para>
/// 用法：<c>new SettingsWindow(settingsService).ShowDialog()</c>。
/// 点"保存"会把界面值写入新的 <see cref="AppSettings"/> 并通过 <see cref="SettingsService.Save(AppSettings)"/> 落盘，
/// 然后触发 <see cref="Saved"/> 事件并以 <c>DialogResult = true</c> 关闭；点"取消"不落盘。
/// </para>
/// <para>
/// 主题：窗口自身的 <c>ThemeMode</c> 随主题选择实时预览；保存时通过
/// <see cref="ThemeManager.Apply(string?)"/> 应用到整个应用。
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>开机自启在注册表 Run 表中的值名。</summary>
    private const string AppName = "WinSnap";

    private readonly SettingsService _settings;
    private bool _loading; // 初始化加载期间抑制主题预览等事件副作用

    /// <summary>
    /// 保存成功后触发。订阅方应据此应用需要在配置落盘后生效的运行时状态。
    /// </summary>
    public event EventHandler? Saved;

    /// <summary>
    /// 保存时若任一全局热键发生变化，则额外触发本事件，参数为新的截图 / 钉图 / GIF 录制热键。
    /// 订阅方应在返回 true 前完成注册；返回 false 时本窗口不落盘。
    /// </summary>
    public event Func<string, string, string, bool>? HotkeysChanged;

    /// <summary>
    /// 创建设置窗口。<paramref name="settings"/> 应为应用共享的同一 <see cref="SettingsService"/>
    /// 实例（已 <c>Load()</c>），保存会写回它。
    /// </summary>
    public SettingsWindow(SettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeComponent();
        LoadFromSettings();
    }

    /// <summary>把当前配置填充到各控件。</summary>
    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;

            // ① 主题
            switch ((s.Theme ?? "system").Trim().ToLowerInvariant())
            {
                case "light": ThemeLightRadio.IsChecked = true; break;
                case "dark": ThemeDarkRadio.IsChecked = true; break;
                default: ThemeSystemRadio.IsChecked = true; break;
            }
            ApplyPreviewTheme(s.Theme);

            // ② 截图热键
            CaptureHotkeyRecorder.Hotkey = s.CaptureHotkey ?? string.Empty;
            PinHotkeyRecorder.Hotkey = s.PinHotkey ?? string.Empty;
            GifHotkeyRecorder.Hotkey = s.GifCaptureHotkey ?? string.Empty;

            // ③ 开机自启：以注册表实际状态为准（与配置可能不同步时，注册表优先反映真实情况）
            string? exe = Environment.ProcessPath;
            AutoStartCheck.IsChecked = !string.IsNullOrEmpty(exe)
                && AutoStartHelper.IsEnabled(AppName, exe);

            // ④ 保存格式与目录
            bool isJpg = string.Equals(s.DefaultSaveFormat, "jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.DefaultSaveFormat, "jpeg", StringComparison.OrdinalIgnoreCase);
            FormatJpgRadio.IsChecked = isJpg;
            FormatPngRadio.IsChecked = !isJpg;
            JpegQualityTextBox.Text = ClampJpegQuality(s.JpegQuality).ToString(CultureInfo.InvariantCulture);
            SaveDirTextBox.Text = s.LastSaveDirectory ?? string.Empty;

            // ⑤ HDR 色调映射
            double hdrSdrWhite = ClampHdrSdrWhite(s.HdrSdrWhiteLevelNits);
            HdrSdrWhiteTextBox.Text = hdrSdrWhite.ToString("0", CultureInfo.InvariantCulture);
            HdrPeakTextBox.Text = ClampHdrPeak(s.HdrPeakNits, hdrSdrWhite).ToString("0", CultureInfo.InvariantCulture);

            // ⑥ GIF 录制
            GifDefaultDurationTextBox.Text = ClampGifDuration(s.GifDefaultDurationSeconds).ToString();
            GifCountdownTextBox.Text = ClampGifCountdown(s.GifCountdownSeconds).ToString();
            GifFpsTextBox.Text = ClampGifFps(s.GifFramesPerSecond).ToString();

            // ⑦ 放大镜
            MagnifierCheck.IsChecked = s.ShowMagnifier;

            // ⑧ 诊断
            LoggingCheck.IsChecked = s.EnableLogging;

            // ⑨ 关于
            VersionTextBlock.Text = $"版本 {GetAppVersion()}";
        }
        finally
        {
            _loading = false;
        }
    }

    // ---------------------------------------------------------------------
    // 主题：实时预览（仅作用于本窗口）
    // ---------------------------------------------------------------------

    private void OnThemeRadioChecked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        ApplyPreviewTheme(SelectedTheme());
    }

    /// <summary>把指定主题应用到本窗口（预览用，不影响全局，直到保存）。</summary>
    private void ApplyPreviewTheme(string? theme)
    {
#pragma warning disable WPF0001 // ThemeMode 为实验性 API
        // LHS 用 this. 限定，避免与 System.Windows.ThemeMode 类型名歧义（与官方示例一致）。
        this.ThemeMode = ThemeManager.ToThemeMode(theme);
#pragma warning restore WPF0001
    }

    /// <summary>当前选中的主题字符串：system / light / dark。</summary>
    private string SelectedTheme()
    {
        if (ThemeLightRadio.IsChecked == true) return "light";
        if (ThemeDarkRadio.IsChecked == true) return "dark";
        return "system";
    }

    // ---------------------------------------------------------------------
    // 保存目录浏览
    // ---------------------------------------------------------------------

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        // .NET 8+ WPF 原生文件夹选择对话框（避免引入 WinForms 类型）
        var dialog = new OpenFolderDialog
        {
            Title = "选择默认保存目录",
        };

        string current = SaveDirTextBox.Text;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) == true)
            SaveDirTextBox.Text = dialog.FolderName;
    }

    // ---------------------------------------------------------------------
    // 保存 / 取消
    // ---------------------------------------------------------------------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var s = _settings.Current;

        string theme = SelectedTheme();

        // 记录变更前的热键，便于判断是否需要通知重注册。截图热键不允许清空。
        string oldCaptureHotkey = s.CaptureHotkey ?? string.Empty;
        string oldPinHotkey = s.PinHotkey ?? string.Empty;
        string oldGifHotkey = s.GifCaptureHotkey ?? string.Empty;
        string requestedCaptureHotkey = string.IsNullOrWhiteSpace(CaptureHotkeyRecorder.Hotkey)
            ? oldCaptureHotkey
            : CaptureHotkeyRecorder.Hotkey;
        string requestedPinHotkey = PinHotkeyRecorder.Hotkey ?? string.Empty;
        string requestedGifHotkey = GifHotkeyRecorder.Hotkey ?? string.Empty;

        string saveFormat = FormatJpgRadio.IsChecked == true ? "jpg" : "png";
        string? saveDirectory = string.IsNullOrWhiteSpace(SaveDirTextBox.Text)
            ? null
            : SaveDirTextBox.Text;

        if (!TryReadIntRange(JpegQualityTextBox.Text, 1, 100, "JPG 质量", out int jpegQuality))
            return;
        if (!TryReadDoubleRange(HdrSdrWhiteTextBox.Text, 80.0, 1000.0, "SDR 白点亮度", out double hdrSdrWhite))
            return;
        if (!TryReadDoubleRange(HdrPeakTextBox.Text, hdrSdrWhite, 4000.0, "HDR 峰值亮度", out double hdrPeak))
            return;

        // ⑤ GIF 录制
        if (!TryReadIntRange(GifDefaultDurationTextBox.Text, 1, 60, "默认录制秒数", out int gifDuration))
            return;
        if (!TryReadIntRange(GifCountdownTextBox.Text, 0, 10, "倒计时秒数", out int gifCountdown))
            return;
        if (!TryReadIntRange(GifFpsTextBox.Text, 1, 20, "每秒帧数（FPS）", out int gifFps))
            return;

        bool hotkeysChanged =
            !string.Equals(oldCaptureHotkey, requestedCaptureHotkey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(oldPinHotkey, requestedPinHotkey, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(oldGifHotkey, requestedGifHotkey, StringComparison.OrdinalIgnoreCase);
        if (hotkeysChanged && HotkeysChanged is { } hotkeysChangedHandler &&
            !hotkeysChangedHandler.Invoke(requestedCaptureHotkey, requestedPinHotkey, requestedGifHotkey))
        {
            MessageBox.Show(this,
                "全局热键注册失败，可能已被其它程序占用。设置未保存，原热键已保留。",
                "热键被占用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // ③ 开机自启：写注册表，并同步 RunAtStartup 字段。失败不阻断保存。
        bool wantAutoStart = AutoStartCheck.IsChecked == true;
        ApplyAutoStart(wantAutoStart);

        var updated = new AppSettings
        {
            Theme = theme,
            CaptureHotkey = requestedCaptureHotkey,
            PinHotkey = requestedPinHotkey,
            ScrollCaptureHotkey = string.Empty,
            GifCaptureHotkey = requestedGifHotkey,
            DefaultSaveFormat = saveFormat,
            JpegQuality = jpegQuality,
            LastSaveDirectory = saveDirectory,
            HdrSdrWhiteLevelNits = hdrSdrWhite,
            HdrPeakNits = hdrPeak,
            GifDefaultDurationSeconds = gifDuration,
            GifCountdownSeconds = gifCountdown,
            GifFramesPerSecond = gifFps,
            ShowMagnifier = MagnifierCheck.IsChecked == true,
            EnableLogging = LoggingCheck.IsChecked == true,
            RunAtStartup = wantAutoStart,
        };

        // 落盘
        try
        {
            _settings.Save(updated);
        }
        catch (Exception ex)
        {
            if (hotkeysChanged && HotkeysChanged is { } restoreHotkeys)
                restoreHotkeys.Invoke(oldCaptureHotkey, oldPinHotkey, oldGifHotkey);

            MessageBox.Show(this,
                ex.Message,
                "保存设置失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 应用主题到整个应用
        ThemeManager.Apply(theme);

        Saved?.Invoke(this, EventArgs.Empty);

        DialogResult = true;
        Close();
    }

    /// <summary>根据开关写注册表 Run 表；异常以消息框提示但不抛出。</summary>
    private void ApplyAutoStart(bool enable)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;

        try
        {
            if (enable)
                AutoStartHelper.Enable(AppName, exe, arguments: null);
            else
                AutoStartHelper.Disable(AppName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"设置开机自启失败：{ex.Message}",
                "WinSnap",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private bool TryReadIntRange(string text, int min, int max, string label, out int value)
    {
        if (int.TryParse(text.Trim(), out value) && value >= min && value <= max)
            return true;

        MessageBox.Show(this,
            $"{label} 需要填写 {min} 到 {max} 之间的整数。",
            "WinSnap",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private bool TryReadDoubleRange(string text, double min, double max, string label, out double value)
    {
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max)
                return true;
        }

        MessageBox.Show(this,
            $"{label} 需要填写 {min:0} 到 {max:0} 之间的数字。",
            "WinSnap",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private static int ClampGifDuration(int value) => Math.Clamp(value, 1, 60);

    private static int ClampGifCountdown(int value) => Math.Clamp(value, 0, 10);

    private static int ClampGifFps(int value) => Math.Clamp(value, 1, 20);

    private static int ClampJpegQuality(int value) => Math.Clamp(value, 1, 100);

    private static double ClampHdrSdrWhite(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 300.0 : Math.Clamp(value, 80.0, 1000.0);

    private static double ClampHdrPeak(double value, double sdrWhite)
        => double.IsNaN(value) || double.IsInfinity(value) ? 1000.0 : Math.Clamp(value, sdrWhite, 4000.0);

    private static string GetAppVersion()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "unknown";
    }
}
