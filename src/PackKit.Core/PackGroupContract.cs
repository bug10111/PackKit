namespace PackKit.Core;

/// <summary>
/// 分组契约边界收口工具：清单（<see cref="ManifestJson"/>）与制作端工程文件两类 JSON 反序列化边界共用，
/// 单源保证分组集合与条目分组引用的结构不变量，下游消费方不再各自判空兜底。
/// </summary>
public static class PackGroupContract
{
    /// <summary>
    /// 把分组集合收口为契约形态：null 集合收口为空集合；剔除 null 条目与标识 / 名称空缺的分组；
    /// 标识重复的分组保留首个（分组标识是条目引用锚点，重复即结构损坏，只认首个，保持原顺序即创建顺序）。
    /// </summary>
    /// <param name="groups">反序列化得到的原始分组集合（可能为 null 或含半成品条目）。</param>
    /// <returns>结构不变量已兑现的分组集合。</returns>
    public static List<PackGroup> NormalizeGroups(IEnumerable<PackGroup?>? groups)
    {
        var normalized = new List<PackGroup>();
        if (groups is null)
        {
            return normalized;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group is null)
            {
                // 边界收口口径：非法 null 条目直接剔除，不向下游透传半成品对象
                continue;
            }

            group.Id ??= string.Empty;
            group.Name ??= string.Empty;

            // 标识或名称空缺的分组无法在成品端左栏呈现，按边界口径剔除
            if (string.IsNullOrWhiteSpace(group.Id) || string.IsNullOrWhiteSpace(group.Name))
            {
                continue;
            }

            if (!seenIds.Add(group.Id))
            {
                continue;
            }

            normalized.Add(group);
        }

        return normalized;
    }

    /// <summary>
    /// 把条目所属分组标识收口为契约形态：空白或未命中有效分组集合的引用收口为 null（未分组），
    /// 防止悬空引用流入下游导致条目在任何分组视图都无法呈现。
    /// </summary>
    /// <param name="groupId">反序列化得到的条目分组标识。</param>
    /// <param name="validGroupIds">当前清单 / 工程内有效的分组标识集合。</param>
    /// <returns>契约合法的分组标识；null 表示未分组。</returns>
    public static string? NormalizeGroupId(string? groupId, HashSet<string> validGroupIds)
    {
        if (string.IsNullOrWhiteSpace(groupId) || !validGroupIds.Contains(groupId))
        {
            return null;
        }

        return groupId;
    }

    /// <summary>
    /// 提取分组标识集合（Ordinal 精确匹配口径，供条目分组引用校验）。
    /// </summary>
    /// <param name="groups">已收口的分组集合。</param>
    /// <returns>分组标识集合。</returns>
    public static HashSet<string> CollectGroupIds(IReadOnlyList<PackGroup> groups)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            ids.Add(group.Id);
        }

        return ids;
    }
}
