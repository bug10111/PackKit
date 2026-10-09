using System.Windows;
using System.Windows.Controls;
using PackKit.Builder.ViewModels;
using PackKit.Core;
using Wpf.Ui.Controls;

namespace PackKit.Builder;

/// <summary>
/// 分组管理对话框：新建、重命名、删除分组（删除时其下安装包回落未分组，不删包）与上移 / 下移调整分组顺序。
/// </summary>
public partial class GroupManagerDialog : FluentWindow
{
    private readonly MainViewModel viewModel;

    /// <summary>
    /// 创建分组管理对话框并绑定工程视图模型。
    /// </summary>
    /// <param name="viewModel">主界面视图模型（分组集合与安装包条目的宿主）。</param>
    public GroupManagerDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;

        // 分组集合增删移动后刷新操作按钮可用态；对话框关闭时退订，避免闭包把对话框实例钉在视图模型上
        this.viewModel.Groups.CollectionChanged += OnGroupsCollectionChanged;
        Closed += (_, _) => this.viewModel.Groups.CollectionChanged -= OnGroupsCollectionChanged;
        UpdateActionStates();
    }

    /// <summary>
    /// 分组集合增删移动后的按钮可用态刷新（订阅于构造，关闭对话框时退订）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateActionStates();
    }

    /// <summary>当前选中的分组。</summary>
    private PackGroup? SelectedGroup => GroupsList.SelectedItem as PackGroup;

    /// <summary>
    /// 分组选中变化：名称输入框预填选中分组名（便于直接改名），并刷新操作按钮可用态。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        GroupNameBox.Text = SelectedGroup?.Name ?? string.Empty;
        UpdateActionStates();
    }

    /// <summary>
    /// 新建分组：名称非空且不重名（忽略大小写）时创建，创建后选中新分组便于继续维护。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var name = GroupNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowWarning("请先输入新分组的名称。");
            return;
        }

        if (viewModel.IsGroupNameTaken(name))
        {
            ShowWarning($"分组名「{name}」已存在，同级分组不允许重名。");
            return;
        }

        var created = viewModel.CreateGroup(name);
        GroupsList.SelectedItem = created;
        GroupNameBox.Clear();
    }

    /// <summary>
    /// 重命名选中分组：名称非空且与其他分组不重名（忽略大小写，与自身同名视为无变更）时应用。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } selected)
        {
            ShowWarning("请先在列表中选择要重命名的分组。");
            return;
        }

        var name = GroupNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowWarning("分组名称不能为空。");
            return;
        }

        if (viewModel.IsGroupNameTaken(name) && !string.Equals(selected.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            ShowWarning($"分组名「{name}」已存在，同级分组不允许重名。");
            return;
        }

        viewModel.RenameGroup(selected.Id, name);
    }

    /// <summary>
    /// 删除选中分组：确认提示后删除，其下安装包回落为未分组（不删包）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } selected)
        {
            ShowWarning("请先在列表中选择要删除的分组。");
            return;
        }

        var entryCount = viewModel.CountEntriesInGroup(selected.Id);
        var message = entryCount > 0
            ? $"删除分组「{selected.Name}」？其下 {entryCount} 个安装包将回落为未分组（不会删除安装包）。"
            : $"删除分组「{selected.Name}」？";
        var choice = System.Windows.MessageBox.Show(
            this,
            message,
            "分组管理",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (choice != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        viewModel.DeleteGroup(selected.Id);
        GroupNameBox.Clear();
    }

    /// <summary>
    /// 上移选中分组（越界时不动作）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnMoveUpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } selected)
        {
            viewModel.MoveGroup(selected.Id, -1);
        }
    }

    /// <summary>
    /// 下移选中分组（越界时不动作）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnMoveDownClick(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is { } selected)
        {
            viewModel.MoveGroup(selected.Id, 1);
        }
    }

    /// <summary>
    /// 关闭对话框。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 按选中态刷新操作按钮可用性（重命名 / 删除 / 上下移均依赖选中分组）。
    /// </summary>
    private void UpdateActionStates()
    {
        var hasSelection = SelectedGroup is not null;
        RenameButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
        MoveUpButton.IsEnabled = hasSelection;
        MoveDownButton.IsEnabled = hasSelection;
    }

    /// <summary>
    /// 以可读弹窗提示操作前置条件不满足。
    /// </summary>
    /// <param name="message">提示内容。</param>
    private void ShowWarning(string message)
    {
        System.Windows.MessageBox.Show(
            this,
            message,
            "分组管理",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }
}
