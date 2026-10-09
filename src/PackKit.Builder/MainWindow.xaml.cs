using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PackKit.Builder.Services;
using PackKit.Builder.ViewModels;
using Wpf.Ui.Controls;

namespace PackKit.Builder;

/// <summary>
/// 制作端主窗口：承载界面布局与文件对话框交互，业务状态与动作全部由 <see cref="MainViewModel"/> 承载；
/// 分组的新建、改名与删除在工具条分组下拉面板内完成。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel viewModel = new();

    /// <summary>
    /// 初始化主窗口。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        // 分组下拉面板经属性元素挂在不构成逻辑子级的 Popup 上，弹层内容拿不到窗口的继承上下文，须显式指定数据上下文
        GroupMenuPopup.DataContext = viewModel;
        // 工程随数据目录自动保存：启动时优先恢复数据目录内已存在的工程
        Loaded += OnWindowLoaded;
    }

    /// <summary>
    /// 窗口加载完成后自动载入数据目录内的工程文件；文件不存在保持默认空工程，载入失败以可读反馈降级且不影响当前内容。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var projectPath = MainViewModel.AutoProjectFilePath;
        if (!File.Exists(projectPath))
        {
            return;
        }

        try
        {
            await viewModel.LoadProjectFromFileAsync(projectPath, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            viewModel.ShowError("工程自动载入失败", ex.Message);
        }
    }

    /// <summary>
    /// 归组落位：分组下拉选中某分组时新添加条目自动归入该组；选中「全部」时新添加条目保持未分组。
    /// 两条路径均以反馈条明示归组结果，并把本次批量添加被跳过的文件数并入文案
    /// （跳过警告反馈会被归组结果反馈覆盖，合并展示防信息丢失）。
    /// </summary>
    /// <param name="addedEntries">本次实际入列的条目集合。</param>
    private void HandleAddedEntries(IReadOnlyList<PackageEntryViewModel> addedEntries)
    {
        if (addedEntries.Count == 0)
        {
            return;
        }

        var skippedSuffix = viewModel.LastAddSkippedCount > 0
            ? $"另有 {viewModel.LastAddSkippedCount} 个文件因重复添加或类型受限未添加。"
            : string.Empty;

        if (viewModel.SelectedGroup is { } selectedGroup)
        {
            viewModel.AssignEntriesToGroup(addedEntries, selectedGroup.Id);
            viewModel.ShowSuccess("已添加并归组", $"{addedEntries.Count} 个安装包已归入分组「{selectedGroup.Name}」。{skippedSuffix}");
            return;
        }

        viewModel.ShowInformational("已添加（未分组）", $"{addedEntries.Count} 个安装包保持未分组，可稍后在「编辑参数」中指定分组。{skippedSuffix}");
    }

    /// <summary>
    /// 分组下拉选「全部」：列表恢复显示全部条目并收起面板。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupFilterAllClick(object sender, RoutedEventArgs e)
    {
        viewModel.SelectedGroup = null;
        CloseGroupMenuPopup();
    }

    /// <summary>
    /// 分组下拉点分组名：把筛选范围切到该分组并收起面板。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupFilterItemClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy || sender is not FrameworkElement { DataContext: GroupMenuItemViewModel row })
        {
            return;
        }

        viewModel.SelectedGroup = row.Group;
        CloseGroupMenuPopup();
    }

    /// <summary>
    /// 分组行点重命名：该行进入行内改名编辑态并预填当前名称。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupMenuRenameClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy || sender is not FrameworkElement { DataContext: GroupMenuItemViewModel row })
        {
            return;
        }

        row.EditName = row.Name;
        row.IsEditing = true;
    }

    /// <summary>
    /// 分组行改名确认：名称非空且与其他分组不重名（忽略大小写，与自身同名视为无变更）时应用。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupMenuRenameConfirmClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GroupMenuItemViewModel row })
        {
            ConfirmGroupRename(row);
        }
    }

    /// <summary>
    /// 分组行点删除：确认提示后删除，其下安装包回落为未分组（不删包）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupMenuDeleteClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy || sender is not FrameworkElement { DataContext: GroupMenuItemViewModel row })
        {
            return;
        }

        DeleteGroupWithConfirm(row.Group);
    }

    /// <summary>
    /// 分组改名输入框按键：Enter 确认，Esc 取消。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">按键参数。</param>
    private void OnGroupRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GroupMenuItemViewModel row })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            ConfirmGroupRename(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.IsEditing = false;
            e.Handled = true;
        }
    }

    /// <summary>
    /// 分组改名输入框变为可见时聚焦，便于直接输入。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">可见性变化参数。</param>
    private void OnGroupRenameBoxIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is System.Windows.Controls.TextBox renameBox)
        {
            renameBox.Focus();
            renameBox.SelectAll();
        }
    }

    /// <summary>
    /// 应用分组行内改名（重名校验沿用全工程忽略大小写口径）。
    /// </summary>
    /// <param name="row">处于编辑态的分组行。</param>
    private void ConfirmGroupRename(GroupMenuItemViewModel row)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        var name = row.EditName.Trim();
        if (name.Length == 0)
        {
            ShowGroupMenuWarning("分组名称不能为空。");
            return;
        }

        if (viewModel.IsGroupNameTaken(name) && !string.Equals(row.Group.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            ShowGroupMenuWarning($"分组名「{name}」已存在，同级分组不允许重名。");
            return;
        }

        viewModel.RenameGroup(row.Group.Id, name);
    }

    /// <summary>
    /// 删除分组前确认：提示其下条目将回落未分组，确认后执行删除。
    /// </summary>
    /// <param name="group">待删除分组。</param>
    private void DeleteGroupWithConfirm(PackKit.Core.PackGroup group)
    {
        var entryCount = viewModel.CountEntriesInGroup(group.Id);
        var message = entryCount > 0
            ? $"删除分组「{group.Name}」？其下 {entryCount} 个安装包将回落为未分组（不会删除安装包）。"
            : $"删除分组「{group.Name}」？";
        var choice = System.Windows.MessageBox.Show(
            this,
            message,
            "分组",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (choice != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        viewModel.DeleteGroup(group.Id);
    }

    /// <summary>
    /// 新建分组确认：名称非空且不重名（忽略大小写）时创建。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupCreateConfirmClick(object sender, RoutedEventArgs e)
    {
        ConfirmGroupCreate();
    }

    /// <summary>
    /// 新建分组输入框按键：Enter 确认，Esc 清空输入。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">按键参数。</param>
    private void OnGroupCreateBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ConfirmGroupCreate();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CreateGroupNameBox.Clear();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 应用新建分组（重名校验沿用全工程忽略大小写口径），成功后自动选中新分组为筛选范围并清空输入。
    /// </summary>
    private void ConfirmGroupCreate()
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        var name = CreateGroupNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowGroupMenuWarning("请先输入新分组的名称。");
            return;
        }

        if (viewModel.IsGroupNameTaken(name))
        {
            ShowGroupMenuWarning($"分组名「{name}」已存在，同级分组不允许重名。");
            return;
        }

        var group = viewModel.CreateGroup(name);
        // 新分组自动选中为筛选范围：列表立即只显示该组，后续添加/拖拽的安装包默认归入该组
        viewModel.SelectedGroup = group;
        CreateGroupNameBox.Clear();
        CreateGroupNameBox.Focus();
    }

    /// <summary>分组面板最近一次关闭时刻，用于抑制轻触关闭后同一次按钮点击残触发的重开闪烁（约 200ms 窗口）。</summary>
    private DateTime groupMenuPopupClosedAt = DateTime.MinValue;

    /// <summary>
    /// 分组下拉按钮点击：展开面板；若为面板轻触关闭瞬间残留的同一击则忽略，防「点开又立即关」闪烁。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if ((DateTime.UtcNow - groupMenuPopupClosedAt).TotalMilliseconds < 200)
        {
            return;
        }

        GroupMenuPopup.IsOpen = true;
    }

    /// <summary>
    /// 分组面板关闭：记录关闭时刻供重开闪烁抑制使用。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGroupMenuPopupClosed(object? sender, EventArgs e)
    {
        groupMenuPopupClosedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// 收起分组下拉面板（完成筛选切换后调用）。
    /// </summary>
    private void CloseGroupMenuPopup()
    {
        GroupMenuPopup.IsOpen = false;
    }

    /// <summary>
    /// 以可读弹窗提示分组面板操作前置条件不满足。
    /// </summary>
    /// <param name="message">提示内容。</param>
    private void ShowGroupMenuWarning(string message)
    {
        System.Windows.MessageBox.Show(
            this,
            message,
            "分组",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }

    /// <summary>
    /// 添加安装包：多选 exe / msi 文件入列。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        var settings = AppSettingsService.Current;
        var dialog = new OpenFileDialog
        {
            Title = "添加安装包",
            Filter = "安装包|*.exe;*.msi|所有文件|*.*",
            Multiselect = true,
            InitialDirectory = ResolveInitialDirectory(settings.LastPackageDirectory)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        RememberDirectory(path => path.LastPackageDirectory = Path.GetDirectoryName(dialog.FileNames[0]) ?? string.Empty);
        HandleAddedEntries(viewModel.AddPackageFiles(dialog.FileNames));
    }

    /// <summary>
    /// 添加整个文件夹：取文件夹顶层 exe / msi 入列。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        var settings = AppSettingsService.Current;
        var dialog = new OpenFolderDialog
        {
            Title = "添加整个文件夹（取顶层 exe / msi）",
            InitialDirectory = ResolveInitialDirectory(settings.LastPackageFolder)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        RememberDirectory(path => path.LastPackageFolder = dialog.FolderName);
        HandleAddedEntries(viewModel.AddPackagesFromFolder(dialog.FolderName));
    }

    /// <summary>
    /// 编辑选中条目：弹出对话框收集名称、静默参数与所属分组，确认后回写。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnEditSelectedClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy || viewModel.SelectedItem is not { } selectedEntry)
        {
            return;
        }

        var dialog = new EditPackageDialog(selectedEntry, viewModel.Groups.ToList()) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        viewModel.ApplyEntryEdit(selectedEntry.Id, dialog.EntryName, dialog.EntrySilentArgs, dialog.EntryGroupId);
    }

    /// <summary>
    /// 打开设置对话框：数据目录入口与云备份配置。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        var dialog = new SettingsDialog { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>
    /// 浏览选择可选图标文件。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnBrowseIconClick(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsService.Current;
        var dialog = new OpenFileDialog
        {
            Title = "选择图标（可选）",
            Filter = "图标|*.ico|所有文件|*.*",
            InitialDirectory = ResolveInitialDirectory(settings.LastIconDirectory)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        RememberDirectory(path => path.LastIconDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty);
        viewModel.IconPath = dialog.FileName;
    }

    /// <summary>
    /// 浏览选择壳模板 exe。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnBrowseShellClick(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsService.Current;
        var dialog = new OpenFileDialog
        {
            Title = "选择壳模板",
            Filter = "可执行文件|*.exe",
            InitialDirectory = ResolveInitialDirectory(
                string.IsNullOrEmpty(settings.LastShellDirectory) ? Path.GetDirectoryName(viewModel.ShellExePath) ?? string.Empty : settings.LastShellDirectory)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        RememberDirectory(path => path.LastShellDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty);
        viewModel.ShellExePath = dialog.FileName;
    }

    /// <summary>
    /// 浏览选择成品输出位置。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnBrowseOutputClick(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsService.Current;
        var dialog = new SaveFileDialog
        {
            Title = "选择成品输出位置",
            Filter = "可执行文件|*.exe",
            InitialDirectory = ResolveInitialDirectory(settings.LastOutputDirectory),
            FileName = SanitizeDialogFileName(Path.GetFileName(viewModel.OutputExePath))
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        RememberDirectory(path => path.LastOutputDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty);
        viewModel.OutputExePath = dialog.FileName;
    }

    /// <summary>
    /// 列表拖拽悬停：仅当拖入内容包含文件列表时接受复制。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">拖拽事件参数。</param>
    private void OnPackagesDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 列表拖拽落下：把拖入的 exe / msi 文件加入列表（重复与不受支持文件跳过并明示）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">拖拽事件参数。</param>
    private void OnPackagesDrop(object sender, DragEventArgs e)
    {
        if (viewModel.IsBusy || e.Data.GetData(DataFormats.FileDrop) is not string[] droppedFiles)
        {
            return;
        }

        HandleAddedEntries(viewModel.AddPackageFiles(droppedFiles));
        e.Handled = true;
    }

    /// <summary>
    /// 窗口关闭时取消进行中的打包，避免后台任务持有已关闭界面的回调目标。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">取消事件参数。</param>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        viewModel.CancelPack();
        // 工程随数据目录自动保存：关闭前兜底补写一次，防最后一次变更的自动保存尚在收尾
        if (viewModel.IsDirty)
        {
            viewModel.SaveProjectAuto();
        }
    }

    /// <summary>
    /// 打开项目开源地址（经系统默认浏览器导航到 GitHub 仓库主页）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnGitHubClick(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/bug10111/PackKit")
        {
            UseShellExecute = true
        });
    }

    /// <summary>
    /// 解析对话框初始目录（记忆目录不存在时回退桌面）。
    /// </summary>
    /// <param name="rememberedDirectory">记忆目录。</param>
    /// <returns>可用的初始目录。</returns>
    private static string ResolveInitialDirectory(string rememberedDirectory)
    {
        if (!string.IsNullOrWhiteSpace(rememberedDirectory) && Directory.Exists(rememberedDirectory))
        {
            return rememberedDirectory;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrEmpty(desktop) ? string.Empty : desktop;
    }

    /// <summary>
    /// 清洗对话框默认文件名中的非法字符。
    /// </summary>
    /// <param name="rawFileName">原始文件名。</param>
    /// <returns>合法文件名。</returns>
    private static string SanitizeDialogFileName(string rawFileName)
    {
        var sanitized = rawFileName.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidChar, '-');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "未命名.packkit.json" : sanitized;
    }

    /// <summary>
    /// 记忆对话框目录并落盘。
    /// </summary>
    /// <param name="update">目录更新动作。</param>
    private static void RememberDirectory(Action<Models.AppSettings> update)
    {
        AppSettingsService.RememberDirectory(update);
    }
}
