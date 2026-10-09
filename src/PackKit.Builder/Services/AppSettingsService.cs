using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PackKit.Builder.Models;

namespace PackKit.Builder.Services;

/// <summary>
/// 本地设置持久化服务：负责把最近目录记忆读写到用户应用数据目录（读写失败不阻断主链路，设置仅为辅助信息）。
/// </summary>
public static class AppSettingsService
{
    private static readonly object FileGate = new();

    /// <summary>
    /// 取当前设置快照（线程安全读取）。
    /// </summary>
    /// <returns>当前设置实例。</returns>
    public static AppSettings Current
    {
        get
        {
            lock (FileGate)
            {
                return _current;
            }
        }
    }

    private static AppSettings _current = new();

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    /// <summary>
    /// 构造设置 JSON 序列化选项（camelCase 属性名，可读缩进便于人工排查）。
    /// </summary>
    /// <returns>序列化选项实例。</returns>
    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// 取设置文件路径（%APPDATA%\PackKit\Builder\settings.json）。
    /// </summary>
    /// <returns>设置文件完整路径。</returns>
    private static string GetSettingsFilePath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PackKit",
            "Builder");
        return Path.Combine(directory, "settings.json");
    }

    /// <summary>
    /// 从磁盘加载设置；文件缺失或内容损坏时保持默认设置并在界面层可见场景记录（设置损坏不阻断使用）。
    /// </summary>
    public static void Load()
    {
        lock (FileGate)
        {
            try
            {
                var filePath = GetSettingsFilePath();
                if (!File.Exists(filePath))
                {
                    return;
                }

                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (loaded is null)
                {
                    return;
                }

                loaded.LastProjectDirectory ??= string.Empty;
                loaded.LastPackageDirectory ??= string.Empty;
                loaded.LastPackageFolder ??= string.Empty;
                loaded.LastIconDirectory ??= string.Empty;
                loaded.LastShellDirectory ??= string.Empty;
                loaded.LastOutputDirectory ??= string.Empty;
                _current = loaded;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 设置读取失败仅退回默认值：目录记忆属辅助信息，主链路（工程编辑与打包）不依赖它；失败原因留痕调试通道
                Debug.WriteLine($"PackKit.Builder 本地设置读取失败，已回退默认设置：{ex.Message}");
                _current = new AppSettings();
            }
        }
    }

    /// <summary>
    /// 把当前设置写入磁盘；写入失败静默降级为「本次不记忆」，不向用户报错阻断操作。
    /// </summary>
    public static void Save()
    {
        lock (FileGate)
        {
            try
            {
                var filePath = GetSettingsFilePath();
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(filePath, JsonSerializer.Serialize(_current, SerializerOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录记忆写入失败不影响功能使用，仅放弃本次记忆；失败原因留痕调试通道
                Debug.WriteLine($"PackKit.Builder 本地设置写入失败，已放弃本次目录记忆：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 记录一次对话框使用后的目录并立即落盘。
    /// </summary>
    /// <param name="update">对设置实例的原地修改动作。</param>
    public static void RememberDirectory(Action<AppSettings> update)
    {
        lock (FileGate)
        {
            update(_current);
        }

        Save();
    }
}
