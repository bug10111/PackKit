namespace PackKit.Player.ViewModels;

/// <summary>
/// 成品端左栏分组筛选形态。
/// </summary>
public enum GroupFilterKind
{
    /// <summary>全部条目（跨分组，恒为左栏首项）。</summary>
    All,

    /// <summary>指定分组（按分组标识匹配）。</summary>
    SpecificGroup,

    /// <summary>未分组条目（仅存在未分组条目时出现在左栏）。</summary>
    Ungrouped
}
