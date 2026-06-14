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
/// 点"保存"会把界面值写回 <see cref="SettingsService.Current"/> 并 <see cref="SettingsService.Save()"/>，
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
    /// 保存成功后触发。订阅方应据此重注册全局热键并应用主题
    /// （例如读 <see cref="SettingsService.Current"/> 调用
    /// <c>HotkeyManager.RegisterCaptureHotkey(settings.Current.CaptureHotkey)</c>）。
    /// </summary>
    public event EventHandler? Saved;

    /// <summary>
    /// 保存时若截图热键发生了变化，则额外触发本事件，参数为新的热键字符串
    /// （如 <c>"Ctrl+Alt+A"</c>）。订阅方可直接据此重注册，无需再读配置。
    /// 未变化则不触发。
    /// </summary>
    public event Action<string>? CaptureHotkeyChanged;

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

            // ③ 开机自启：以注册表实际状态为准（与配置可能不同步时，注册表优先反映真实情况）
            string? exe = Environment.ProcessPath;
            AutoStartCheck.IsChecked = !string.IsNullOrEmpty(exe)
                && AutoStartHelper.IsEnabled(AppName, exe);

            // ④ 保存格式与目录
            bool isJpg = string.Equals(s.DefaultSaveFormat, "jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.DefaultSaveFormat, "jpeg", StringComparison.OrdinalIgnoreCase);
            FormatJpgRadio.IsChecked = isJpg;
            FormatPngRadio.IsChecked = !isJpg;
            SaveDirTextBox.Text = s.LastSaveDirectory ?? string.Empty;

            // ⑤ GIF 录制
            GifDefaultDurationTextBox.Text = ClampGifDuration(s.GifDefaultDurationSeconds).ToString();
            GifCountdownTextBox.Text = ClampGifCountdown(s.GifCountdownSeconds).ToString();
            GifFpsTextBox.Text = ClampGifFps(s.GifFramesPerSecond).ToString();

            // ⑥ 放大镜
            MagnifierCheck.IsChecked = s.ShowMagnifier;

            // ⑦ 关于
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

        // 记录变更前的热键，便于判断是否需要通知重注册。
        string oldHotkey = s.CaptureHotkey ?? string.Empty;

        // ① 主题
        string theme = SelectedTheme();
        s.Theme = theme;

        // ② 截图热键：仅当录入了合法（非空）值时才覆盖，避免误清空导致无快捷键。
        string newHotkey = CaptureHotkeyRecorder.Hotkey;
        if (!string.IsNullOrWhiteSpace(newHotkey))
            s.CaptureHotkey = newHotkey;

        // ④ 保存格式与目录
        s.DefaultSaveFormat = FormatJpgRadio.IsChecked == true ? "jpg" : "png";
        s.LastSaveDirectory = string.IsNullOrWhiteSpace(SaveDirTextBox.Text)
            ? null
            : SaveDirTextBox.Text;

        // ⑤ GIF 录制
        if (!TryReadIntRange(GifDefaultDurationTextBox.Text, 1, 60, "默认录制秒数", out int gifDuration))
            return;
        if (!TryReadIntRange(GifCountdownTextBox.Text, 0, 10, "倒计时秒数", out int gifCountdown))
            return;
        if (!TryReadIntRange(GifFpsTextBox.Text, 1, 20, "每秒帧数（FPS）", out int gifFps))
            return;

        s.GifDefaultDurationSeconds = gifDuration;
        s.GifCountdownSeconds = gifCountdown;
        s.GifFramesPerSecond = gifFps;

        // ⑥ 放大镜
        s.ShowMagnifier = MagnifierCheck.IsChecked == true;

        // ③ 开机自启：写注册表，并同步 RunAtStartup 字段。失败不阻断保存。
        bool wantAutoStart = AutoStartCheck.IsChecked == true;
        ApplyAutoStart(wantAutoStart);
        s.RunAtStartup = wantAutoStart;

        // 落盘
        _settings.Save();

        // 应用主题到整个应用
        ThemeManager.Apply(theme);

        // 通知订阅方
        if (!string.Equals(oldHotkey, s.CaptureHotkey, StringComparison.OrdinalIgnoreCase))
            CaptureHotkeyChanged?.Invoke(s.CaptureHotkey ?? string.Empty);
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

    private static int ClampGifDuration(int value) => Math.Clamp(value, 1, 60);

    private static int ClampGifCountdown(int value) => Math.Clamp(value, 0, 10);

    private static int ClampGifFps(int value) => Math.Clamp(value, 1, 20);

    private static string GetAppVersion()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "unknown";
    }
}
