using System.IO;

namespace PackKit.Builder.Services;

/// <summary>
/// 壳模板定位服务：按「随包查找」约定从制作端程序位置出发定位成品端 Player exe 默认路径。
/// </summary>
public static class ShellLocator
{
    /// <summary>成品端壳程序文件名。</summary>
    public const string ShellFileName = "PackKit.Player.exe";

    /// <summary>
    /// 按约定查找壳模板默认路径：优先与制作端同目录，其次自程序目录逐级向上在 dist 部署目录中查找。
    /// </summary>
    /// <returns>找到的壳模板完整路径；找不到返回空串（由用户手动浏览指定）。</returns>
    public static string FindDefault()
    {
        var baseDirectory = AppContext.BaseDirectory;

        var sideBySidePath = Path.Combine(baseDirectory, ShellFileName);
        if (File.Exists(sideBySidePath))
        {
            return sideBySidePath;
        }

        var candidateDirectory = new DirectoryInfo(baseDirectory);
        for (var depth = 0; depth < 6 && candidateDirectory is not null; depth++)
        {
            var distPath = Path.Combine(candidateDirectory.FullName, "dist", "PackKit.Player", ShellFileName);
            if (File.Exists(distPath))
            {
                return distPath;
            }

            candidateDirectory = candidateDirectory.Parent;
        }

        return string.Empty;
    }
}
