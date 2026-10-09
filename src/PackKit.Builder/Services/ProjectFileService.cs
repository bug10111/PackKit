using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PackKit.Builder.Models;
using PackKit.Core;

namespace PackKit.Builder.Services;

/// <summary>
/// 工程文件读写服务：把 <see cref="BuilderProject"/> 与 .packkit.json 相互转换，并在加载边界兑现结构不变量。
/// </summary>
public static class ProjectFileService
{
    /// <summary>
    /// 构造工程文件 JSON 序列化选项（camelCase 属性名 + 可读缩进）。
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

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    /// <summary>
    /// 保存工程到指定文件（UTF-8 覆盖写）。
    /// </summary>
    /// <param name="project">待保存工程。</param>
    /// <param name="filePath">目标文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public static async Task SaveAsync(BuilderProject project, string filePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(project, SerializerOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    /// <summary>
    /// 从指定文件加载工程；在边界兑现结构不变量：未知格式版本与非法结构拒绝，
    /// 集合与字符串字段空缺收口为契约默认值，分组集合与条目分组引用做引用完整性收口，下游不再各自判空兜底。
    /// </summary>
    /// <param name="filePath">工程文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结构不变量已兑现的工程实例。</returns>
    /// <exception cref="InvalidDataException">文件缺失、格式版本未知或结构无法收口时抛出。</exception>
    /// <exception cref="JsonException">JSON 语法或结构与契约不匹配时抛出（调用方界面层按容错降级处理）。</exception>
    public static async Task<BuilderProject> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("工程文件不存在。", filePath);
        }

        var json = await File.ReadAllTextAsync(filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("工程文件内容为空，无法解析。");
        }

        var project = JsonSerializer.Deserialize<BuilderProject>(json, SerializerOptions);
        if (project is null)
        {
            throw new InvalidDataException("工程文件解析结果为空，无法继续。");
        }

        if (project.FormatVersion != BuilderProject.CurrentFormatVersion)
        {
            throw new InvalidDataException($"工程文件格式版本 {project.FormatVersion} 不受支持（当前支持版本 {BuilderProject.CurrentFormatVersion}）。");
        }

        NormalizeInvariants(project);
        return project;
    }

    /// <summary>
    /// 反序列化边界收口：分组集合与条目集合 null 收口为空集合、剔除 null 条目与非法分组、
    /// 条目字符串字段空缺补契约默认值、图标字段空白收口为 null（契约口径：IconPng 只有「null=无图标」与「非空 base64 文本」两种合法形态）、
    /// 条目分组引用空白或未命中分组集合收口为 null（未分组），分组集合收口细则见 <see cref="PackGroupContract"/>。
    /// </summary>
    /// <param name="project">待收口的工程实例。</param>
    private static void NormalizeInvariants(BuilderProject project)
    {
        project.Groups = PackGroupContract.NormalizeGroups(project.Groups);
        var validGroupIds = PackGroupContract.CollectGroupIds(project.Groups);

        var normalizedItems = new List<BuilderProjectItem>();
        foreach (var item in project.Items)
        {
            if (item is null)
            {
                // 非法 null 条目直接剔除，不向下游透传半成品对象
                continue;
            }

            item.Id ??= string.Empty;
            item.Name ??= string.Empty;
            item.SourceFilePath ??= string.Empty;
            item.InstallerType ??= "unknown";
            item.SilentArgs ??= string.Empty;
            item.IconPng = string.IsNullOrWhiteSpace(item.IconPng) ? null : item.IconPng;
            item.GroupId = PackGroupContract.NormalizeGroupId(item.GroupId, validGroupIds);
            normalizedItems.Add(item);
        }

        project.Items = normalizedItems;
        project.Title ??= string.Empty;
        project.IconPath ??= string.Empty;
        project.ShellExePath ??= string.Empty;
        project.OutputExePath ??= string.Empty;
    }
}
