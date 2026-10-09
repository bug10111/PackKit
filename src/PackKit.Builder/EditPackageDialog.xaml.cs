using System.Windows;
using PackKit.Builder.ViewModels;
using PackKit.Core;
using Wpf.Ui.Controls;

namespace PackKit.Builder;

/// <summary>
/// 安装包条目编辑对话框：名称、静默参数与所属分组可改，安装包文件与类型只读展示。
/// </summary>
public partial class EditPackageDialog : FluentWindow
{
    /// <summary>分组下拉选项（纯数据，未分组为合法显式状态且恒为首选）。</summary>
    /// <param name="GroupId">分组唯一标识；null 表示未分组。</param>
    /// <param name="DisplayName">下拉展示名。</param>
    private sealed record GroupChoice(string? GroupId, string DisplayName);

    /// <summary>
    /// 创建编辑对话框并按当前条目预填（分组下拉默认选中条目当前所属分组，未分组条目选中「未分组」）。
    /// </summary>
    /// <param name="entry">待编辑条目。</param>
    /// <param name="groups">当前工程的全部分组（按创建顺序）。</param>
    public EditPackageDialog(PackageEntryViewModel entry, IReadOnlyList<PackGroup> groups)
    {
        InitializeComponent();
        NameBox.Text = entry.Name;
        FileNameText.Text = entry.FileName;
        InstallerTypeText.Text = entry.InstallerTypeDisplay;
        SilentArgsBox.Text = entry.SilentArgs;

        var choices = new List<GroupChoice>
        {
            new(null, PackageEntryViewModel.UngroupedDisplay)
        };
        foreach (var group in groups)
        {
            choices.Add(new GroupChoice(group.Id, group.Name));
        }

        GroupCombo.ItemsSource = choices;
        GroupCombo.SelectedItem = choices.FirstOrDefault(choice => string.Equals(choice.GroupId, entry.GroupId, StringComparison.Ordinal)) ?? choices[0];
    }

    /// <summary>
    /// 取用户确认后的名称。
    /// </summary>
    public string EntryName => NameBox.Text;

    /// <summary>
    /// 取用户确认后的静默参数。
    /// </summary>
    public string EntrySilentArgs => SilentArgsBox.Text;

    /// <summary>
    /// 取用户确认后的所属分组唯一标识；null 表示未分组。
    /// </summary>
    public string? EntryGroupId => (GroupCombo.SelectedItem as GroupChoice)?.GroupId;

    /// <summary>
    /// 取消并关闭对话框。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    /// 确认编辑：名称不允许为空（空名称在成品端清单无法辨识），空值时给出可理解提示不关闭。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            System.Windows.MessageBox.Show(
                this,
                "名称不能为空，请填写安装包的展示名称。",
                "编辑安装包",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
