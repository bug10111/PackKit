namespace PackKit.Builder.Models;

using PackKit.Core;

/// <summary>
/// 制作端工程文件模型：一个待打包工程 = 成品设置 + 软件分组集合 + 有序安装包条目列表，持久化为 .packkit.json。
/// </summary>
public sealed class BuilderProject
{
    /// <summary>工程文件格式版本，结构演进时递增；加载边界拒绝未知版本。</summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>成品标题（打包时写入清单 Title，成品端窗口标题展示）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>可选图标 .ico 文件路径；空串表示不嵌入图标。</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>壳模板 exe 路径（成品端 Player 程序）。</summary>
    public string ShellExePath { get; set; } = string.Empty;

    /// <summary>成品 exe 输出路径。</summary>
    public string OutputExePath { get; set; } = string.Empty;

    /// <summary>
    /// 软件分组集合（顺序即创建顺序；条目经 <see cref="BuilderProjectItem.GroupId"/> 引用分组标识）。
    /// 空集合表示未使用分组；旧工程文件无该字段时反序列化为空集合。
    /// </summary>
    public List<PackGroup> Groups { get; set; } = new();

    /// <summary>安装包条目有序列表（顺序即成品端安装顺序）。</summary>
    public List<BuilderProjectItem> Items { get; set; } = new();

    /// <summary>当前工程文件格式版本。</summary>
    public const int CurrentFormatVersion = 1;
}

/// <summary>
/// 工程内的单条安装包条目：记录源文件位置、展示信息与安装方式。
/// </summary>
public sealed class BuilderProjectItem
{
    /// <summary>条目唯一标识（Guid 字符串），打包时透传给清单条目 Id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>展示名称（成品端清单界面显示）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>源安装包文件绝对路径。</summary>
    public string SourceFilePath { get; set; } = string.Empty;

    /// <summary>安装器类型字符串（与 PackKit.Core InstallerType 枚举字符串形式一致：nsis/inno/msi/unknown）。</summary>
    public string InstallerType { get; set; } = "unknown";

    /// <summary>静默安装参数；空串表示无静默参数（成品端人工点下一步）。</summary>
    public string SilentArgs { get; set; } = string.Empty;

    /// <summary>
    /// 条目图标的 base64 PNG 文本（入列时从 exe 图标资源抽取，打包时透传给清单条目 IconPng）；
    /// null 表示无图标。旧工程文件无该字段时反序列化为 null。
    /// </summary>
    public string? IconPng { get; set; }

    /// <summary>
    /// 所属分组唯一标识（指向工程 <see cref="BuilderProject.Groups"/> 中的分组）；null 表示未分组。
    /// 旧工程文件无该字段时反序列化为 null（全部未分组）。
    /// </summary>
    public string? GroupId { get; set; }
}
