using System.Text;

namespace PackKit.Core;

/// <summary>
/// 安装器类型启发式探测器：读取文件头部窗口字节做魔数与标识串匹配，识别不了返回 unknown 不猜测。
/// </summary>
public static class InstallerTypeDetector
{
    /// <summary>头部探测窗口字节长度（文件头 64KB 内做标识串扫描）。</summary>
    public const int HeaderScanLength = 64 * 1024;

    private static readonly byte[] MsiHeaderMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly byte[] NsisMarkerBytes = Encoding.ASCII.GetBytes("Nullsoft");

    private static readonly byte[] InnoMarkerBytes = Encoding.ASCII.GetBytes("Inno Setup");

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
            _ => string.Empty
        };
    }

    /// <summary>
    /// 在头部窗口内按优先级判定安装器类型：MSI 复合文档魔数优先，其次 NSIS / Inno 标识串。
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

        // NSIS：firstheader 内含 "Nullsoft" 标识串
        if (window.IndexOf(NsisMarkerBytes) >= 0)
        {
            return InstallerType.Nsis;
        }

        // Inno Setup：头部窗口内含 "Inno Setup" 标识串
        if (window.IndexOf(InnoMarkerBytes) >= 0)
        {
            return InstallerType.Inno;
        }

        return InstallerType.Unknown;
    }
}
