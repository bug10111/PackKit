using System.IO;
using System.IO.Compression;
using PackKit.Core;

namespace PackKit.Builder.Services;

/// <summary>
/// 云同步备份服务：数据本体恒在本地数据目录（exe 旁 data\），备份目的地为用户百度云同步盘资料目录；
/// 启动时与每日定时巡检，距上次备份超过 24 小时即把数据目录打包为关闭态快照 zip 写入备份目录，
/// 保留最近 14 份自动轮转；备份失败不阻塞主流程，落日志留痕。
/// </summary>
public static class CloudBackupService
{
    /// <summary>备份触发间隔：距上次成功备份超过该时长即触发一次备份。</summary>
    private static readonly TimeSpan BackupInterval = TimeSpan.FromHours(24);

    /// <summary>巡检定时器周期：定时唤醒做到期判定。</summary>
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromMinutes(30);

    /// <summary>备份快照保留份数：超出后按时间从旧到新自动轮转删除。</summary>
    private const int RetainedSnapshotCount = 14;

    /// <summary>备份 zip 文件名模式（前缀），同时用于轮转时识别本服务的快照文件。</summary>
    private const string SnapshotFileNamePrefix = "PackKit-data-";

    private static System.Threading.Timer? sweepTimer;

    /// <summary>
    /// 取默认备份目录（用户百度云同步盘资料目录下按项目名建子目录；经用户文档目录解析，随登录用户适配）。
    /// </summary>
    /// <returns>默认备份目录绝对路径。</returns>
    public static string GetDefaultBackupRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "BaiduSyncdisk",
            "java相关",
            "资料",
            "PackKit");
    }

    /// <summary>
    /// 取当前生效备份目录：设置了自定义目录且非空时用自定义值，否则用默认目录。
    /// </summary>
    /// <returns>当前生效备份目录绝对路径。</returns>
    public static string GetEffectiveBackupRoot()
    {
        var settings = AppSettingsService.Current;
        var overrideRoot = settings.BackupDirectoryOverride?.Trim();
        return string.IsNullOrEmpty(overrideRoot) ? GetDefaultBackupRoot() : overrideRoot;
    }

    /// <summary>
    /// 启动每日巡检：立即做一次启动时到期判定，并注册组件自有的定时巡检与进程退出资源释放
    /// （生命周期兜底由组件自身登记，不依赖宿主装配点代注册）。
    /// </summary>
    public static void StartDailyWatch()
    {
        if (sweepTimer is not null)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        sweepTimer = new System.Threading.Timer(
            _ => RunIfDueSafe(),
            null,
            SweepPeriod,
            SweepPeriod);

        // 启动时到期判定放后台线程执行，不阻塞界面启动
        _ = System.Threading.Tasks.Task.Run(RunIfDueSafe);
    }

    /// <summary>
    /// 执行一次到期判定：距上次成功备份超过 24 小时才真正备份。
    /// </summary>
    public static void RunIfDue()
    {
        var lastBackupAt = AppSettingsService.Current.LastCloudBackupAt;
        if (lastBackupAt is not null && DateTime.Now - lastBackupAt.Value < BackupInterval)
        {
            return;
        }

        BackupNow();
    }

    /// <summary>
    /// 立即执行一次备份：停写窗口内打包数据目录为快照 zip，随后更新备份时间并轮转旧快照。
    /// 任何失败只落日志，不向界面与主流程抛出。
    /// </summary>
    public static void BackupNow()
    {
        var tempPath = string.Empty;
        try
        {
            var backupRoot = GetEffectiveBackupRoot();
            var sourceRoot = DataDirectoryService.CurrentRoot;
            if (!Directory.Exists(sourceRoot) || !Directory.EnumerateFileSystemEntries(sourceRoot).Any())
            {
                // 数据目录尚无内容（首次安装未产生设置）时无保护对象，跳过本次备份
                AppLogger.Info("数据目录暂无内容，跳过本次云备份。");
                return;
            }

            Directory.CreateDirectory(backupRoot);
            var snapshotPath = Path.Combine(
                backupRoot,
                $"{SnapshotFileNamePrefix}{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            tempPath = snapshotPath + ".tmp";

            // 停写窗口内取关闭态快照：zip 期间设置写入挂起，保证快照内部一致
            AppSettingsService.ExecuteWithSuspendedWrites(() =>
            {
                ZipFile.CreateFromDirectory(sourceRoot, tempPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                File.Move(tempPath, snapshotPath, overwrite: false);
            });

            AppSettingsService.RememberDirectory(settings => settings.LastCloudBackupAt = DateTime.Now);
            var snapshotSize = new FileInfo(snapshotPath).Length;
            AppLogger.Info($"云备份完成：{snapshotPath}（{snapshotSize / 1024.0:F1} KB）。");
            RotateOldSnapshots(backupRoot);
        }
        catch (Exception ex)
        {
            // 备份失败不阻塞主流程：清理可能残留的临时 zip 后留痕日志，等待下一轮巡检重试
            TryDeleteTempSnapshot(tempPath);
            AppLogger.Error("云备份执行失败。", ex);
        }
    }

    /// <summary>
    /// 清理备份失败可能残留的临时 zip 文件（删除失败留痕跳过，不影响后续轮次）。
    /// </summary>
    /// <param name="tempPath">临时 zip 文件路径。</param>
    private static void TryDeleteTempSnapshot(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning($"云备份临时文件清理失败：{tempPath}：{ex.Message}");
        }
    }

    /// <summary>
    /// 定时巡检入口：吞掉一切异常保证定时器线程存活。
    /// </summary>
    private static void RunIfDueSafe()
    {
        try
        {
            RunIfDue();
        }
        catch (Exception ex)
        {
            AppLogger.Error("云备份巡检发生未预期异常。", ex);
        }
    }

    /// <summary>
    /// 轮转旧快照：按最后写入时间从新到旧保留固定份数，其余删除；单个删除失败跳过不中断。
    /// </summary>
    /// <param name="backupRoot">备份目录。</param>
    private static void RotateOldSnapshots(string backupRoot)
    {
        // 枚举与排序分步书写：先取快照文件清单，再按最后写入时间从新到旧排序
        var snapshotFilePaths = Directory.EnumerateFiles(backupRoot, $"{SnapshotFileNamePrefix}*.zip").ToList();
        var snapshotFiles = snapshotFilePaths.OrderByDescending(File.GetLastWriteTime).ToList();
        for (var index = RetainedSnapshotCount; index < snapshotFiles.Count; index++)
        {
            try
            {
                File.Delete(snapshotFiles[index]);
                AppLogger.Info($"云备份轮转删除过期快照：{snapshotFiles[index]}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warning($"云备份轮转删除失败（保留待下轮）：{snapshotFiles[index]}：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 进程退出兜底：释放巡检定时器（组件自身登记，不依赖宿主装配点代注册）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        sweepTimer?.Dispose();
        sweepTimer = null;
    }
}
