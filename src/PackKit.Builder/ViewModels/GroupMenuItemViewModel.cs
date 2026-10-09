using PackKit.Core;

namespace PackKit.Builder.ViewModels;

/// <summary>
/// 分组下拉面板行视图模型：一行对应一个分组，承载行展示态与行内改名编辑态
/// （PackGroup 为 Core 冻结契约无属性通知，界面行状态由此包装承载）。
/// </summary>
public sealed class GroupMenuItemViewModel : ObservableObject
{
    private string name;

    private bool isSelected;

    private bool isEditing;

    private string editName = string.Empty;

    /// <summary>
    /// 创建分组行视图模型。
    /// </summary>
    /// <param name="group">对应分组实例（筛选与改名、删除动作均按其 Id 定位）。</param>
    public GroupMenuItemViewModel(PackGroup group)
    {
        Group = group;
        name = group.Name;
    }

    /// <summary>对应分组实例。</summary>
    public PackGroup Group { get; }

    /// <summary>分组展示名。</summary>
    public string Name
    {
        get => name;
        set => SetProperty(ref name, value);
    }

    /// <summary>当前分组范围是否为本行分组（下拉面板内以对勾图标冗余标注，不单靠颜色表达）。</summary>
    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    /// <summary>是否处于行内改名编辑态（编辑态行展示输入框，常态行展示名称与维护按钮）。</summary>
    public bool IsEditing
    {
        get => isEditing;
        set => SetProperty(ref isEditing, value);
    }

    /// <summary>行内改名输入框当前文本。</summary>
    public string EditName
    {
        get => editName;
        set => SetProperty(ref editName, value);
    }
}
