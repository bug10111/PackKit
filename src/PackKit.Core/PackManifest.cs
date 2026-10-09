namespace PackKit.Core;

/// <summary>
/// 成品包清单：写入成品 exe 内 zip 的 manifest.json 根条目，描述成品标题与全部安装包条目。
/// </summary>
public sealed class PackManifest
{
    /// <summary>清单格式版本，当前契约恒为 <see cref="PayloadFormat.CurrentFormatVersion"/>（1）。</summary>
    public int FormatVersion { get; set; } = PayloadFormat.CurrentFormatVersion;

    /// <summary>成品标题（成品端窗口标题与清单页头展示）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>安装包条目集合（按安装顺序排列；反序列化边界保证非空集合不变量）。</summary>
    public List<PackItem> Items { get; set; } = new();

    /// <summary>
    /// 软件分组集合（按创建顺序排列；条目经 <see cref="PackItem.GroupId"/> 引用分组标识）。
    /// 反序列化边界保证非空集合不变量；空集合表示无分组数据（成品端左栏整体隐藏，与旧成品一致）。
    /// </summary>
    public List<PackGroup> Groups { get; set; } = new();

    /// <summary>清单创建时间（制作端生成清单时写入）。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
