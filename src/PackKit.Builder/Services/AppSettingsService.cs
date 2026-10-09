using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PackKit.Builder.Models;
using PackKit.Core;

namespace PackKit.Builder.Services;

/// <summary>
/// 本地设置持久化服务：设置文件收敛于数据目录（exe 旁 data\，支持迁移），
/// 旧版 %APPDATA% 设置在首次运行时一次性导入；读写失败不阻断主链路，设置仅为辅助信息。
/// </summary>
public static class AppSettingsService
{
    /// <summary>旧版设置文件位置（%APPDATA%）：数据目录收敛前历史安装的存放位置，仅作为一次性导入来源。</summary>
    private static readonly string LegacySettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PackKit",
        "Builder",
        "settings.json");
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
    /// 取设置文件路径（当前生效数据目录内 settings.json，路径解析见 <see cref="DataDirectoryService"/>）。
    /// </summary>
    /// <returns>设置文件完整路径。</returns>
    private static string GetSettingsFilePath()
    {
        return DataDirectoryService.GetSettingsFilePath();
    }

    /// <summary>
    /// 在停止设置的写入窗口内执行动作（迁移/云备份等需要数据目录关闭态快照的场景使用）。
    /// </summary>
    /// <param name="action">停写窗口内执行的动作。</param>
    public static void ExecuteWithSuspendedWrites(Action action)
    {
        lock (FileGate)
        {
            action();
        }
    }

    /// <summary>
    /// 旧版设置一次性导入：数据目录内尚无设置文件而旧版 %APPDATA% 设置存在时，复制旧文件到数据目录后按正常加载读取。
    /// </summary>
    /// <param name="targetFilePath">数据目录内设置文件路径。</param>
    private static void ImportLegacySettingsIfPresent(string targetFilePath)
    {
        try
        {
            if (File.Exists(targetFilePath) || !File.Exists(LegacySettingsFilePath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
            File.Copy(LegacySettingsFilePath, targetFilePath, overwrite: false);
            AppLogger.Info($"已从旧版位置导入设置：{LegacySettingsFilePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 导入失败不阻断启动：旧目录记忆属辅助信息，丢失仅影响首次目录记忆
            AppLogger.Warning($"旧版设置导入失败（按默认设置继续）：{ex.Message}");
        }
    }

    /// <summary>
    /// 从磁盘加载设置；文件缺失或内容损坏时保持默认设置并留痕（设置损坏不阻断使用）。
    /// </summary>
    public static void Load()
    {
        lock (FileGate)
        {
            try
            {
                var filePath = GetSettingsFilePath();
                ImportLegacySettingsIfPresent(filePath);
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

                loaded.LastPackageDirectory ??= string.Empty;
                loaded.LastPackageFolder ??= string.Empty;
                loaded.LastIconDirectory ??= string.Empty;
                loaded.LastShellDirectory ??= string.Empty;
                loaded.LastOutputDirectory ??= string.Empty;
                loaded.BackupDirectoryOverride ??= string.Empty;
                _current = loaded;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 设置读取失败仅退回默认值：目录记忆属辅助信息，主链路（工程编辑与打包）不依赖它
                AppLogger.Warning($"本地设置读取失败，已回退默认设置：{ex.Message}");
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
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

                File.WriteAllText(filePath, JsonSerializer.Serialize(_current, SerializerOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录记忆写入失败不影响功能使用，仅放弃本次记忆
                AppLogger.Warning($"本地设置写入失败，已放弃本次目录记忆：{ex.Message}");
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
