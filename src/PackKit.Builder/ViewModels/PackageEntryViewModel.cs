using System.IO;
using PackKit.Core;

namespace PackKit.Builder.ViewModels;

/// <summary>
/// 安装包列表行视图模型：一行对应一条安装包条目，承载列表展示字段与可编辑字段。
/// </summary>
public sealed class PackageEntryViewModel : ObservableObject
{
    /// <summary>未分组条目在列表分组列的固定展示文本。</summary>
    public const string UngroupedDisplay = "未分组";

    private string name = string.Empty;

    private string silentArgs = string.Empty;

    private string? groupId;

    private string groupName = string.Empty;

    private int index;

    /// <summary>
    /// 创建列表行视图模型。
    /// </summary>
    /// <param name="id">条目唯一标识（Guid 字符串）。</param>
    /// <param name="name">展示名称。</param>
    /// <param name="sourceFilePath">源安装包文件路径。</param>
    /// <param name="installerType">安装器类型（入列时探测）。</param>
    /// <param name="silentArgs">静默安装参数（入列时按类型预填）。</param>
    /// <param name="sizeBytes">源文件字节长度。</param>
    /// <param name="iconPng">条目图标的 base64 PNG 文本（入列时从 exe 抽取；null 表示无图标）。</param>
    /// <param name="groupId">所属分组唯一标识；null 表示未分组。</param>
    /// <param name="groupName">所属分组展示名（与 groupId 同步传入）。</param>
    public PackageEntryViewModel(string id, string name, string sourceFilePath, InstallerType installerType, string silentArgs, long sizeBytes, string? iconPng = null, string? groupId = null, string groupName = "")
    {
        Id = id;
        Name = name;
        SourceFilePath = sourceFilePath;
        InstallerType = installerType;
        SilentArgs = silentArgs;
        SizeBytes = sizeBytes;
        IconPng = iconPng;
        GroupId = groupId;
        GroupName = groupName;
    }

    /// <summary>条目唯一标识（Guid 字符串）。</summary>
    public string Id { get; }

    /// <summary>源安装包文件路径。</summary>
    public string SourceFilePath { get; }

    /// <summary>安装器类型（探测结果，编辑时只读展示）。</summary>
    public InstallerType InstallerType { get; }

    /// <summary>源文件字节长度。</summary>
    public long SizeBytes { get; }

    /// <summary>条目图标的 base64 PNG 文本（入列时从 exe 图标资源抽取；null 表示无图标，成品端显示占位字形）。</summary>
    public string? IconPng { get; }

    /// <summary>展示名称（用户可编辑）。</summary>
    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    /// <summary>静默安装参数（用户可编辑；空串表示人工点下一步）。</summary>
    public string SilentArgs
    {
        get => silentArgs;
        set => SetProperty(ref silentArgs, value);
    }

    /// <summary>所属分组唯一标识；null 表示未分组。</summary>
    public string? GroupId
    {
        get => groupId;
        set
        {
            if (SetProperty(ref groupId, value))
            {
                OnPropertyChanged(nameof(GroupDisplay));
            }
        }
    }

    /// <summary>所属分组展示名（与 GroupId 同步维护，由宿主视图模型在分组增删改时刷新）。</summary>
    public string GroupName
    {
        get => groupName;
        set
        {
            if (SetProperty(ref groupName, value))
            {
                OnPropertyChanged(nameof(GroupDisplay));
            }
        }
    }

    /// <summary>列表分组列展示文本（未分组条目固定显示「未分组」）。</summary>
    public string GroupDisplay => string.IsNullOrEmpty(GroupId) ? UngroupedDisplay : GroupName;

    /// <summary>列表序号（1 起，随增删移动维护）。</summary>
    public int Index
    {
        get => index;
        private set => SetProperty(ref index, value);
    }

    /// <summary>源文件纯文件名（决定 zip 内条目名）。</summary>
    public string FileName
    {
        get
        {
            var fileName = Path.GetFileName(SourceFilePath);
            return string.IsNullOrEmpty(fileName) ? SourceFilePath : fileName;
        }
    }

    /// <summary>人类可读的文件大小（如 1.6 MB）。</summary>
    public string SizeDisplay => FormatSize(SizeBytes);

    /// <summary>安装器类型的人类可读展示名（无法识别显示「未知」不猜测）。</summary>
    public string InstallerTypeDisplay
    {
        get
        {
            return InstallerType switch
            {
                InstallerType.Nsis => "NSIS",
                InstallerType.Inno => "Inno Setup",
                InstallerType.Msi => "MSI",
                _ => "未知"
            };
        }
    }

    /// <summary>
    /// 维护列表序号（由集合宿主在增删移动后调用）。
    /// </summary>
    /// <param name="newIndex">新序号（1 起）。</param>
    public void UpdateIndex(int newIndex)
    {
        Index = newIndex;
    }

    /// <summary>
    /// 把字节长度格式化为人类可读大小（B/KB/MB/GB 自适应，保留一位小数）。
    /// </summary>
    /// <param name="sizeBytes">字节长度。</param>
    /// <returns>格式化文本。</returns>
    public static string FormatSize(long sizeBytes)
    {
        const long kilo = 1024;
        const long mega = kilo * 1024;
        const long giga = mega * 1024;

        return sizeBytes switch
        {
            >= giga => $"{(double)sizeBytes / giga:0.#} GB",
            >= mega => $"{(double)sizeBytes / mega:0.#} MB",
            >= kilo => $"{(double)sizeBytes / kilo:0.#} KB",
            _ => $"{sizeBytes} B"
        };
    }
}
