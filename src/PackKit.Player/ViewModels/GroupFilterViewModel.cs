using PackKit.Core;

namespace PackKit.Player.ViewModels;

/// <summary>
/// 成品端左栏分组筛选项：筛选形态 + 分组标识 + 展示文本。
/// 计数在清单加载时定格（清单运行期只读），故不承载变更通知。
/// </summary>
public sealed class GroupFilterViewModel
{
    /// <summary>
    /// 创建左栏筛选选项。
    /// </summary>
    /// <param name="kind">筛选形态。</param>
    /// <param name="groupId">分组标识（仅特定分组形态有值）。</param>
    /// <param name="title">展示名（全部 / 分组名 / 未分组）。</param>
    /// <param name="count">该范围内的安装包条目数。</param>
    public GroupFilterViewModel(GroupFilterKind kind, string? groupId, string title, int count)
    {
        Kind = kind;
        GroupId = groupId;
        Title = title;
        Count = count;
    }

    /// <summary>筛选形态。</summary>
    public GroupFilterKind Kind { get; }

    /// <summary>分组标识（仅 <see cref="GroupFilterKind.SpecificGroup"/> 形态有值，对应清单 <see cref="PackManifest.Groups"/> 内分组）。</summary>
    public string? GroupId { get; }

    /// <summary>展示名。</summary>
    public string Title { get; }

    /// <summary>该范围内的安装包条目数（清单加载时定格）。</summary>
    public int Count { get; }

    /// <summary>左栏展示文本（形如「全部 12」「开发 5」「未分组 1」，名称与计数以空格分隔）。</summary>
    public string Display => $"{Title} {Count}";
}
