using System.Text;

namespace PackKit.Core;

/// <summary>
/// 安装器类型启发式探测器：读取文件头部窗口字节做魔数与标识串匹配，识别不了返回 unknown 不猜测。
/// </summary>
public static class InstallerTypeDetector
{
    /// <summary>头部探测窗口字节长度（文件头 8MB 内做标识串扫描；NSIS/Inno 的特征串位于 PE 桩之后、压缩数据之前，实测均在头部近邻，8MB 覆盖各类加壳变体）。</summary>
    public const int HeaderScanLength = 8 * 1024 * 1024;

    private static readonly byte[] MsiHeaderMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly byte[] NsisStrongMarkerBytes = Encoding.ASCII.GetBytes("NullsoftInst");

    private static readonly byte[] NsisMarkerBytes = Encoding.ASCII.GetBytes("Nullsoft");

    private static readonly byte[] InnoMarkerBytes = Encoding.ASCII.GetBytes("Inno Setup");

    private static readonly byte[] InstallShieldMarkerBytes = Encoding.ASCII.GetBytes("InstallShield");

    private static readonly byte[] SquirrelMarkerBytes = Encoding.ASCII.GetBytes("Squirrel");

    /// <summary>
    /// 探测磁盘文件（探测 MSI 头魔数与 NSIS / Inno 标识串）。
    /// </summary>
    /// <param name="filePath">待探测文件路径。</param>
    /// <returns>探测出的安装器类型；无法识别时为 <see cref="InstallerType.Unknown"/>。</returns>
    public static InstallerType Detect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return Detect(fileStream);
    }

    /// <summary>
    /// 探测流内容（流必须可 Seek，方法返回后流位置复原）。
    /// </summary>
    /// <param name="fileStream">待探测的只读流。</param>
    /// <returns>探测出的安装器类型；无法识别时为 <see cref="InstallerType.Unknown"/>。</returns>
    public static InstallerType Detect(Stream fileStream)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        if (!fileStream.CanSeek)
        {
            throw new ArgumentException("探测流必须支持 Seek。", nameof(fileStream));
        }

        long originalPosition = fileStream.Position;
        var headerBytes = new byte[HeaderScanLength];
        int totalRead = 0;
        while (totalRead < headerBytes.Length)
        {
            int read = fileStream.Read(headerBytes, totalRead, headerBytes.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        fileStream.Seek(originalPosition, SeekOrigin.Begin);
        return DetectFromHeader(headerBytes, totalRead);
    }

    /// <summary>
    /// 取安装器类型的默认静默安装参数（制作端给用户预填；用户可改，unknown 恒为空串即人工点下一步）。
    /// </summary>
    /// <param name="installerType">安装器类型。</param>
    /// <returns>默认静默参数（含前导空格分隔的多参数）；unknown 返回空串。</returns>
    public static string GetDefaultSilentArgs(InstallerType installerType)
    {
        return installerType switch
        {
            InstallerType.Nsis => "/S",
            InstallerType.Inno => "/VERYSILENT /NORESTART",
            InstallerType.Msi => "/qn /norestart",
            InstallerType.InstallShield => "/s /v\"/qn\"",
            InstallerType.Squirrel => "--silent",
            _ => string.Empty
        };
    }

    /// <summary>
    /// 在头部窗口内按优先级判定安装器类型：MSI 复合文档魔数优先，其次各安装器标识串
    /// （NSIS 强特征 "NullsoftInst" 优先于弱特征 "Nullsoft"，弱特征单独出现可能是其他软件携带的字符串）。
    /// </summary>
    /// <param name="headerBytes">头部读取缓冲区。</param>
    /// <param name="headerLength">实际读到的字节数。</param>
    /// <returns>探测出的安装器类型。</returns>
    private static InstallerType DetectFromHeader(byte[] headerBytes, int headerLength)
    {
        if (headerLength >= MsiHeaderMagic.Length)
        {
            // MSI：OLE 复合文档魔数固定位于文件头 8 字节
            if (headerBytes.AsSpan(0, MsiHeaderMagic.Length).SequenceEqual(MsiHeaderMagic))
            {
                return InstallerType.Msi;
            }
        }

        var window = headerBytes.AsSpan(0, headerLength);

        // NSIS：firstheader 内固定含 "NullsoftInst" 魔数串（强特征）
        if (window.IndexOf(NsisStrongMarkerBytes) >= 0)
        {
            return InstallerType.Nsis;
        }

        // Inno Setup：头部窗口内含 "Inno Setup" 标识串
        if (window.IndexOf(InnoMarkerBytes) >= 0)
        {
            return InstallerType.Inno;
        }

        // InstallShield：头部窗口内含 "InstallShield" 标识串
        if (window.IndexOf(InstallShieldMarkerBytes) >= 0)
        {
            return InstallerType.InstallShield;
        }

        // Squirrel：Electron 系应用（Squirrel.Windows 打包）安装器内含 "Squirrel" 标识串
        if (window.IndexOf(SquirrelMarkerBytes) >= 0)
        {
            return InstallerType.Squirrel;
        }

        // NSIS：弱特征兜底（部分变体 firstheader 被改写仅余 "Nullsoft" 字样）
        if (window.IndexOf(NsisMarkerBytes) >= 0)
        {
            return InstallerType.Nsis;
        }

        return InstallerType.Unknown;
    }
}
