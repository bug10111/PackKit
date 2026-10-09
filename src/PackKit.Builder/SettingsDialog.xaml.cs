using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PackKit.Builder.Services;
using PackKit.Core;
using Wpf.Ui.Controls;

namespace PackKit.Builder;

/// <summary>
/// 设置对话框：承载数据目录三项入口（打开数据目录/迁移数据目录/打开日志目录）与云同步备份目录配置。
/// </summary>
public partial class SettingsDialog : FluentWindow
{
    /// <summary>
    /// 初始化对话框并填充当前数据目录、备份目录与备份状态。
    /// </summary>
    public SettingsDialog()
    {
        InitializeComponent();
        RefreshDataRootDisplay();
        var settings = AppSettingsService.Current;
        BackupDirBox.Text = settings.BackupDirectoryOverride;
        DefaultBackupText.Text = $"默认目录：{CloudBackupService.GetDefaultBackupRoot()}";
        LastBackupText.Text = settings.LastCloudBackupAt is { } lastBackupAt
            ? $"上次备份：{lastBackupAt:yyyy-MM-dd HH:mm:ss}"
            : "上次备份：尚未备份";
    }

    /// <summary>
    /// 刷新数据目录显示（迁移成功后同步生效路径）。
    /// </summary>
    private void RefreshDataRootDisplay()
    {
        DataRootText.Text = $"当前目录：{DataDirectoryService.CurrentRoot}";
    }

    /// <summary>
    /// 打开数据目录：确保目录存在后用资源管理器打开。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnOpenDataDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DataDirectoryService.EnsureRoot();
            OpenDirectoryInExplorer(DataDirectoryService.CurrentRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Error("打开数据目录失败。", ex);
            System.Windows.MessageBox.Show(
                this,
                $"打开数据目录失败：{ex.Message}",
                "设置",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 迁移数据目录：选择新目录并确认后执行迁移，成功刷新显示，失败提示原因（原数据保持原位）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnMigrateDataDirClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择迁移目标目录（须为空目录）"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var choice = System.Windows.MessageBox.Show(
            this,
            $"迁移后设置与本地数据将整体移动到：\n{dialog.FolderName}\n\n迁移过程失败会自动保持原位。是否继续？",
            "迁移数据目录",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);
        if (choice != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            DataDirectoryService.Migrate(dialog.FolderName);
            RefreshDataRootDisplay();
            System.Windows.MessageBox.Show(
                this,
                "数据目录迁移完成。",
                "设置",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLogger.Error("数据目录迁移失败（已保持原位）。", ex);
            System.Windows.MessageBox.Show(
                this,
                $"数据目录迁移失败，原数据保持原位：\n{ex.Message}",
                "设置",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 打开日志目录：确保目录存在后用资源管理器打开。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLogger.LogDirectory);
            OpenDirectoryInExplorer(AppLogger.LogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Error("打开日志目录失败。", ex);
            System.Windows.MessageBox.Show(
                this,
                $"打开日志目录失败：{ex.Message}",
                "设置",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 浏览选择备份目录并回填输入框（关闭对话框时统一落盘）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnBrowseBackupDirClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择云备份目录"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BackupDirBox.Text = dialog.FolderName;
    }

    /// <summary>
    /// 关闭对话框：把备份目录配置写入设置并落盘。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        PersistBackupDirectoryOverride();
        Close();
    }

    /// <summary>
    /// 窗口关闭兜底：覆盖标题栏关闭按钮等不经关闭按钮的退出路径，保证备份目录配置不丢。
    /// </summary>
    /// <param name="e">关闭事件参数。</param>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        PersistBackupDirectoryOverride();
        base.OnClosing(e);
    }

    /// <summary>
    /// 把备份目录输入框内容写入设置（空白视为恢复默认目录）。
    /// </summary>
    private void PersistBackupDirectoryOverride()
    {
        AppSettingsService.RememberDirectory(settings => settings.BackupDirectoryOverride = BackupDirBox.Text.Trim());
    }

    /// <summary>
    /// 用资源管理器打开目录。
    /// </summary>
    /// <param name="directoryPath">目标目录。</param>
    private static void OpenDirectoryInExplorer(string directoryPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = directoryPath,
            UseShellExecute = true
        });
    }
}
