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

    /// <summary>最近一次加载配置失败的异常；成功加载时为 null。</summary>
    public Exception? LastLoadError { get; private set; }

    /// <summary>最近一次保存配置失败的异常；成功保存时为 null。</summary>
    public Exception? LastSaveError { get; private set; }

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
            LastLoadError = null;
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
            catch (Exception ex)
            {
                // 配置损坏 -> 回退默认，不抛出
                LastLoadError = ex;
                Current = new AppSettings();
            }

            return Current;
        }
    }

    /// <summary>用指定配置覆盖并持久化。</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            LastSaveError = null;
            try
            {
                SaveInternal(settings);
                Current = settings;
            }
            catch (Exception ex)
            {
                LastSaveError = ex;
                TryDeleteTempFile();
                throw new IOException($"保存设置失败：{_path}", ex);
            }
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

    private void TryDeleteTempFile()
    {
        try
        {
            var tmp = _path + ".tmp";
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
        catch
        {
            // 保存失败时清理临时文件只是尽力而为，不能覆盖原始异常。
        }
    }
}
