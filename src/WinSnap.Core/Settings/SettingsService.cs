using System.Text.Json;

namespace WinSnap.Core.Settings;

/// <summary>
/// 负责 <see cref="AppSettings"/> 的加载与持久化。
/// 路径：%AppData%\WinSnap\settings.json，采用「临时文件 + 替换」原子写防止损坏。
/// 配置损坏时回退默认值，绝不阻断应用启动。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 写本地配置文件：不转义 + / 中文等，保证用户手动查看或编辑时可读
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dir;
    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>当前内存中的配置（始终非 null）。</summary>
    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinSnap");
        _path = Path.Combine(_dir, "settings.json");
    }

    /// <summary>settings.json 的完整路径。</summary>
    public string FilePath => _path;

    /// <summary>从磁盘加载；文件不存在则写入一份默认配置。</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded is not null)
                        Current = loaded;
                }
                else
                {
                    SaveInternal(Current);
                }
            }
            catch
            {
                // 配置损坏 -> 回退默认，不抛出
                Current = new AppSettings();
            }

            return Current;
        }
    }

    /// <summary>保存当前配置。</summary>
    public void Save() => Save(Current);

    /// <summary>用指定配置覆盖并持久化。</summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Current = settings;
            SaveInternal(settings);
        }
    }

    private void SaveInternal(AppSettings settings)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        // 原子写：先写临时文件，再替换目标
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(_path))
            File.Replace(tmp, _path, destinationBackupFileName: null);
        else
            File.Move(tmp, _path);
    }
}
