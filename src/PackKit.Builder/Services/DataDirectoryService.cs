using System.IO;
using PackKit.Core;

namespace PackKit.Builder.Services;

/// <summary>
/// 数据目录服务：数据目录默认锚定 exe 旁 data\（便携哲学，整目录拷走即数据一起迁移）；
/// 支持经迁移流程把数据整体搬移到用户选定目录（指针文件记录生效位置，位于 exe 旁随应用一起走），
/// 迁移全程复制-校验-翻转指针，任一步失败自动保持原位不动。
/// </summary>
public static class DataDirectoryService
{
    /// <summary>指针文件名：位于 exe 旁，内容为迁移后的数据目录绝对路径；不存在即表示使用默认位置。</summary>
    private const string RootPointerFileName = "data-dir.txt";

    /// <summary>迁移互斥闸：与设置写闸（AppSettingsService 写闸）配合，保证迁移与数据写入不并发。</summary>
    private static readonly object MigrationGate = new();

    /// <summary>
    /// 取默认数据目录（exe 旁 data\）。
    /// </summary>
    /// <returns>默认数据目录绝对路径。</returns>
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>
    /// 取当前生效的数据目录：exe 旁指针文件存在且指向合法路径时返回指针目标，否则返回默认目录。
    /// </summary>
    /// <returns>当前生效的数据目录绝对路径。</returns>
    public static string CurrentRoot
    {
        get
        {
            var pointerPath = GetPointerFilePath();
            try
            {
                if (File.Exists(pointerPath))
                {
                    var configuredRoot = File.ReadAllText(pointerPath).Trim();
                    if (Path.IsPathRooted(configuredRoot))
                    {
                        return configuredRoot;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 指针读取失败回落默认目录，不影响主链路；留痕由日志组件承载
                AppLogger.Warning($"数据目录指针读取失败，已回落默认目录：{ex.Message}");
            }

            return DefaultRoot;
        }
    }

    /// <summary>
    /// 取设置文件完整路径（位于当前生效数据目录内）。
    /// </summary>
    /// <returns>settings.json 完整路径。</returns>
    public static string GetSettingsFilePath()
    {
        return Path.Combine(CurrentRoot, "settings.json");
    }

    /// <summary>
    /// 确保当前生效数据目录存在（不存在则创建）；创建失败按异常上抛由调用方按场景处置。
    /// </summary>
    public static void EnsureRoot()
    {
        Directory.CreateDirectory(CurrentRoot);
    }

    /// <summary>
    /// 把当前数据目录整体迁移到新目录：选新目录 → 停写窗口 → 复制 → 校验 → 翻转指针 → 清理旧位；
    /// 翻转指针前任何一步失败都只清理新目录副本，原数据原位不动（自动回滚）。
    /// </summary>
    /// <param name="newRoot">迁移目标目录（须为合法绝对路径，不得与当前目录相同或互为嵌套）。</param>
    /// <exception cref="ArgumentException">目标路径非法、与当前目录相同或互为嵌套。</exception>
    /// <exception cref="InvalidOperationException">目标目录已存在且非空，或复制后校验不一致。</exception>
    public static void Migrate(string newRoot)
    {
        var targetRoot = Path.GetFullPath(newRoot);
        var sourceRoot = CurrentRoot;

        if (string.Equals(Path.GetFullPath(sourceRoot), targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("迁移目标目录与当前数据目录相同。");
        }

        if (IsNestedPath(sourceRoot, targetRoot))
        {
            throw new ArgumentException("迁移目标目录不能与当前数据目录互为嵌套。");
        }

        AppSettingsService.ExecuteWithSuspendedWrites(() =>
        {
            lock (MigrationGate)
            {
                // 重新解析当前目录，避免停写等待期间生效目录被并发迁移改变
                sourceRoot = CurrentRoot;
                if (string.Equals(Path.GetFullPath(sourceRoot), targetRoot, StringComparison.OrdinalIgnoreCase)
                    || IsNestedPath(sourceRoot, targetRoot))
                {
                    throw new ArgumentException("迁移目标目录与当前数据目录相同或互为嵌套。");
                }

                if (Directory.Exists(targetRoot))
                {
                    if (Directory.EnumerateFileSystemEntries(targetRoot).Any())
                    {
                        throw new InvalidOperationException("迁移目标目录已存在且非空，请选择空目录。");
                    }
                }
                else
                {
                    Directory.CreateDirectory(targetRoot);
                }

                AppLogger.Info($"数据目录迁移开始：{sourceRoot} → {targetRoot}");
                try
                {
                    CopyDirectory(sourceRoot, targetRoot);

                    var sourceFileCount = CountFiles(sourceRoot);
                    var targetFileCount = CountFiles(targetRoot);
                    if (sourceFileCount != targetFileCount)
                    {
                        throw new InvalidOperationException($"迁移后校验不一致：源 {sourceFileCount} 个文件，目标 {targetFileCount} 个文件。");
                    }

                    VerifyProbeWrite(targetRoot);

                    // 复制与校验全部通过后才翻转指针：翻转前失败原数据原位不动，翻转即迁移生效
                    WritePointerFile(targetRoot);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException)
                {
                    // 翻转指针前失败：清理目标目录半截副本与指针临时文件后原样上抛，原数据原位不动（自动回滚）
                    CleanupFailedTargetCopy(targetRoot);
                    TryDeletePointerTempFile();
                    throw;
                }

                CleanupAbandonedDirectory(sourceRoot);
                AppLogger.Info($"数据目录迁移完成，生效目录：{targetRoot}");
            }
        });
    }

    /// <summary>
    /// 取指针文件完整路径（exe 旁）。
    /// </summary>
    /// <returns>指针文件完整路径。</returns>
    private static string GetPointerFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, RootPointerFileName);
    }

    /// <summary>
    /// 判断两个路径是否互为嵌套（一方是另一方或其祖先）。
    /// </summary>
    /// <param name="pathA">路径一。</param>
    /// <param name="pathB">路径二。</param>
    /// <returns>互为嵌套时为 true。</returns>
    private static bool IsNestedPath(string pathA, string pathB)
    {
        var fullA = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathA));
        var fullB = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathB));
        return fullB.StartsWith(fullA + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullA.StartsWith(fullB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 递归复制目录全部内容到目标目录（目标须已存在或为空）。
    /// </summary>
    /// <param name="sourceDir">源目录。</param>
    /// <param name="targetDir">目标目录。</param>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDir))
        {
            Directory.CreateDirectory(Path.Combine(targetDir, Path.GetFileName(directoryPath)));
            CopyDirectory(directoryPath, Path.Combine(targetDir, Path.GetFileName(directoryPath)));
        }

        foreach (var filePath in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(filePath, Path.Combine(targetDir, Path.GetFileName(filePath)), overwrite: false);
        }
    }

    /// <summary>
    /// 递归统计目录内文件总数。
    /// </summary>
    /// <param name="directoryPath">目录路径。</param>
    /// <returns>文件总数。</returns>
    private static int CountFiles(string directoryPath)
    {
        return Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).Count();
    }

    /// <summary>
    /// 目标目录探针写入校验：写入-读回-删除探针文件，验证新目录真实可写可读（迁移后可启动的前提）。
    /// </summary>
    /// <param name="directoryPath">待校验目录。</param>
    private static void VerifyProbeWrite(string directoryPath)
    {
        var probePath = Path.Combine(directoryPath, $".migrate-probe-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probePath, "probe");
        try
        {
            if (File.ReadAllText(probePath) != "probe")
            {
                throw new InvalidOperationException("迁移目标目录写入探针读回不一致。");
            }
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    /// <summary>
    /// 原子写出指针文件（临时文件 + 替换），保证指针翻转瞬间一致。
    /// </summary>
    /// <param name="targetRoot">迁移目标目录。</param>
    private static void WritePointerFile(string targetRoot)
    {
        var pointerPath = GetPointerFilePath();
        var tempPath = pointerPath + ".tmp";
        File.WriteAllText(tempPath, targetRoot, System.Text.Encoding.UTF8);
        File.Move(tempPath, pointerPath, overwrite: true);
    }

    /// <summary>
    /// 清理迁移失败后残留在目标目录的半截副本内容（仅清内容、保留目录本身，目录可能为用户预建的空目录）；
    /// 清理失败留痕不抛出，避免掩盖迁移的原始失败原因。
    /// </summary>
    /// <param name="targetRoot">迁移目标目录。</param>
    private static void CleanupFailedTargetCopy(string targetRoot)
    {
        try
        {
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(targetRoot))
            {
                if (Directory.Exists(entryPath))
                {
                    Directory.Delete(entryPath, recursive: true);
                }
                else
                {
                    File.Delete(entryPath);
                }
            }

            AppLogger.Warning($"迁移未完成，已清理目标目录半截副本：{targetRoot}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning($"迁移未完成且目标目录副本清理未成功（不影响原数据）：{targetRoot}：{ex.Message}");
        }
    }

    /// <summary>
    /// 清理指针翻转失败可能残留的指针临时文件（exe 旁 data-dir.txt.tmp）；失败留痕不抛出。
    /// </summary>
    private static void TryDeletePointerTempFile()
    {
        try
        {
            var tempPath = GetPointerFilePath() + ".tmp";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning($"指针临时文件清理失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 清理迁移后遗留的旧目录内容：数据已在新目录生效，清理仅为去残留；清理失败留痕不回滚迁移。
    /// </summary>
    /// <param name="abandonedRoot">迁移前的旧数据目录。</param>
    private static void CleanupAbandonedDirectory(string abandonedRoot)
    {
        try
        {
            Directory.Delete(abandonedRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning($"迁移后旧数据目录清理失败（不影响迁移生效）：{abandonedRoot}：{ex.Message}");
        }
    }
}
