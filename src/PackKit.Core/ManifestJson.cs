using System.Text.Json;
using System.Text.Json.Serialization;

namespace PackKit.Core;

/// <summary>
/// 清单 JSON 序列化边界：统一 camelCase 契约与安装器类型字符串枚举，并在边界兑现非空集合等结构不变量。
/// </summary>
public static class ManifestJson
{
    /// <summary>清单 JSON 的最大允许字符长度，超限视为损坏数据并拒绝解析（防异常膨胀输入）。</summary>
    public const int MaxManifestJsonLength = 4 * 1024 * 1024;

    /// <summary>清单契约专用序列化选项（camelCase 属性名 + 安装器类型字符串枚举，落盘形如 "nsis"/"inno"/"msi"/"unknown"）。</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// 将清单序列化为契约 JSON（camelCase，安装器类型为字符串枚举）。
    /// </summary>
    /// <param name="manifest">待序列化清单。</param>
    /// <returns>契约 JSON 字符串。</returns>
    public static string Serialize(PackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    /// <summary>
    /// 解析契约 JSON 为清单；在边界兑现结构不变量：未知格式版本与超长内容结构化拒绝，
    /// Items 与 Groups 为 null 收口为空集合，条目内空缺字符串字段收口为契约默认值，
    /// 图标字段空白收口为 null，分组引用与分组集合做引用完整性收口。
    /// </summary>
    /// <param name="json">契约 JSON 字符串。</param>
    /// <returns>结构不变量已兑现的清单实例。</returns>
    /// <exception cref="InvalidDataException">JSON 非法、超长、格式版本未知或结构无法收口时抛出。</exception>
    /// <exception cref="JsonException">JSON 语法或结构不匹配契约时抛出（调用方界面层按容错降级处理）。</exception>
    public static PackManifest Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("清单内容为空，无法解析。");
        }

        if (json.Length > MaxManifestJsonLength)
        {
            throw new InvalidDataException($"清单长度 {json.Length} 字符超出上限 {MaxManifestJsonLength}，拒绝解析。");
        }

        var manifest = JsonSerializer.Deserialize<PackManifest>(json, Options);
        if (manifest is null)
        {
            throw new InvalidDataException("清单解析结果为空，无法继续。");
        }

        if (manifest.FormatVersion != PayloadFormat.CurrentFormatVersion)
        {
            throw new InvalidDataException($"清单格式版本 {manifest.FormatVersion} 不受支持（当前契约版本 {PayloadFormat.CurrentFormatVersion}）。");
        }

        NormalizeInvariants(manifest);
        return manifest;
    }

    /// <summary>
    /// 创建清单契约专用的序列化选项。
    /// </summary>
    /// <returns>camelCase + 字符串枚举的序列化选项。</returns>
    private static JsonSerializerOptions CreateOptions()
    {
        var enumConverter = new JsonStringEnumConverter(JsonNamingPolicy.CamelCase);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(enumConverter);
        return options;
    }

    /// <summary>
    /// 反序列化边界收口：分组集合与条目集合 null 收口为空集合、剔除 null 条目与非法分组、
    /// 条目字符串字段空缺补契约默认值、图标字段空白收口为 null（契约口径：IconPng 只有「null=无图标」与「非空 base64 文本」两种合法形态）、
    /// 条目分组引用空白或未命中分组集合收口为 null（未分组），分组集合收口细则见 <see cref="PackGroupContract"/>。
    /// 下游消费方（制作端/成品端）不再各自判空兜底。
    /// </summary>
    /// <param name="manifest">待收口的清单实例。</param>
    private static void NormalizeInvariants(PackManifest manifest)
    {
        manifest.Groups = PackGroupContract.NormalizeGroups(manifest.Groups);
        var validGroupIds = PackGroupContract.CollectGroupIds(manifest.Groups);

        var normalizedItems = new List<PackItem>();
        foreach (var item in manifest.Items)
        {
            if (item is null)
            {
                // 边界收口口径：非法 null 条目直接剔除，不向下游透传半成品对象
                continue;
            }

            item.Id ??= string.Empty;
            item.Name ??= string.Empty;
            item.EntryPath ??= string.Empty;
            item.FileName ??= string.Empty;
            item.SilentArgs ??= string.Empty;
            item.IconPng = string.IsNullOrWhiteSpace(item.IconPng) ? null : item.IconPng;
            item.GroupId = PackGroupContract.NormalizeGroupId(item.GroupId, validGroupIds);
            normalizedItems.Add(item);
        }

        manifest.Items = normalizedItems;
    }
}
