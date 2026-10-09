using System.IO;
using System.Windows;
using Microsoft.Win32;
using PackKit.Builder.Services;
using PackKit.Builder.ViewModels;
using Wpf.Ui.Controls;

namespace PackKit.Builder;

/// <summary>
/// 制作端主窗口：承载界面布局与文件对话框交互，业务状态与动作全部由 <see cref="MainViewModel"/> 承载。
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
    }

    /// <summary>
    /// 新建工程：存在未保存变更时先确认，再重置为默认工程。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        if (viewModel.IsDirty && !ConfirmDiscardChanges())
        {
            return;
        }

        viewModel.ResetProject();
    }

    /// <summary>
    /// 打开工程：选择 .packkit.json 后加载；解析失败以可读反馈呈现且不影响当前内容。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        if (viewModel.IsDirty && !ConfirmDiscardChanges())
        {
            return;
        }

        var settings = AppSettingsService.Current;
        var dialog = new OpenFileDialog
        {
            Title = "打开工程",
            Filter = "PackKit 工程|*.packkit.json|所有文件|*.*",
            InitialDirectory = ResolveInitialDirectory(settings.LastProjectDirectory)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            RememberDirectory(path => path.LastProjectDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty);
            await viewModel.LoadProjectFromFileAsync(dialog.FileName, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            viewModel.ShowError("打开工程失败", ex.Message);
        }
    }

    /// <summary>
    /// 保存工程：选择保存位置后写出 .packkit.json。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private async void OnSaveProjectClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        var settings = AppSettingsService.Current;
        var defaultFileName = $"{viewModel.Title}.packkit.json";
        var dialog = new SaveFileDialog
        {
            Title = "保存工程",
            Filter = "PackKit 工程|*.packkit.json",
            InitialDirectory = ResolveInitialDirectory(settings.LastProjectDirectory),
            FileName = SanitizeDialogFileName(defaultFileName)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            RememberDirectory(path => path.LastProjectDirectory = Path.GetDirectoryName(dialog.FileName) ?? string.Empty);
            await viewModel.SaveProjectToFileAsync(dialog.FileName, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            viewModel.ShowError("保存工程失败", ex.Message);
        }
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
        viewModel.AddPackageFiles(dialog.FileNames);
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
        viewModel.AddPackagesFromFolder(dialog.FolderName);
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
    /// 打开分组管理对话框：新建、重命名、删除分组与调整分组顺序。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnManageGroupsClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        var dialog = new GroupManagerDialog(viewModel) { Owner = this };
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

        viewModel.AddPackageFiles(droppedFiles);
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
    }

    /// <summary>
    /// 未保存变更放弃确认。
    /// </summary>
    /// <returns>用户确认放弃时为 true。</returns>
    private bool ConfirmDiscardChanges()
    {
        var choice = System.Windows.MessageBox.Show(
            this,
            "当前工程有未保存的变更，继续操作将丢失这些变更。是否继续？",
            "装机包制作工具",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        return choice == System.Windows.MessageBoxResult.Yes;
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
