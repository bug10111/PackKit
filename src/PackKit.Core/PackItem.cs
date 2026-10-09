namespace PackKit.Core;

/// <summary>
/// 成品包清单中的一条安装包条目：记录源文件名、zip 内条目路径、大小与安装器类型。
/// </summary>
public sealed class PackItem
{
    /// <summary>条目唯一标识（Guid 字符串），由制作端在加入清单时生成。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>展示名称（成品端清单界面显示），由用户在制作端编辑。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>zip 内条目路径，契约恒为 packages/{FileName}（见 <see cref="PayloadFormat.BuildPackageEntryPath"/>）。</summary>
    public string EntryPath { get; set; } = string.Empty;

    /// <summary>源安装包的纯文件名（不含路径），同时决定 zip 内 packages/ 下的条目名与解压目标文件名。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>源安装包字节长度（解压后长度），制作端写入，成品端用于一致性校验。</summary>
    public long SizeBytes { get; set; }

    /// <summary>安装器类型（文件头启发式探测结果，unknown 不猜测）。</summary>
    public InstallerType InstallerType { get; set; } = InstallerType.Unknown;

    /// <summary>静默安装参数；空串表示无静默参数，安装时由用户人工点下一步完成。</summary>
    public string SilentArgs { get; set; } = string.Empty;

    /// <summary>
    /// 列表行图标的 base64 PNG 文本（制作端从 exe 图标资源抽取）；null 表示无图标，成品端显示统一占位字形。
    /// 随 manifest.json 持久化，不参与 payload 结构；旧清单无该字段时反序列化为 null。
    /// </summary>
    public string? IconPng { get; set; }

    /// <summary>
    /// 所属分组唯一标识（指向清单 <see cref="PackManifest.Groups"/> 中的分组）；null 表示未分组。
    /// 随 manifest.json 持久化；旧清单无该字段时反序列化为 null（全部未分组）。
    /// </summary>
    public string? GroupId { get; set; }
}
