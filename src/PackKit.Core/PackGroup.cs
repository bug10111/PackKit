namespace PackKit.Core;

/// <summary>
/// 成品包清单中的软件分组：成品端左栏按分组过滤显示安装包条目；分组数据随 manifest.json 持久化，顺序即创建顺序。
/// </summary>
public sealed class PackGroup
{
    /// <summary>分组唯一标识（Guid 字符串），由制作端创建分组时生成；条目经 <see cref="PackItem.GroupId"/> 引用本标识。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>分组展示名称（成品端左栏显示）；同级不重名由制作端管理界面保证。</summary>
    public string Name { get; set; } = string.Empty;
}
