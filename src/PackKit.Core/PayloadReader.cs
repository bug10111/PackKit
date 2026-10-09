using System.IO.Compression;
using System.Text;

namespace PackKit.Core;

/// <summary>
/// 成品 exe 载荷读取器：校验尾部 footer、打开 zip 载荷、读回清单并按需提取单个安装包条目。
/// </summary>
public static class PayloadReader
{
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>
    /// 读取并校验成品 exe 文件尾部 footer（文件路径版）。
    /// </summary>
    /// <param name="exePath">成品 exe 路径（允许是正在运行中的自身 exe）。</param>
    /// <returns>解析出的 zip 偏移与长度。</returns>
    public static PayloadFooter ReadFooter(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        using var exeStream = OpenExeForRead(exePath);
        return ReadFooter(exeStream);
    }

    /// <summary>
    /// 读取并校验成品 exe 流尾部 footer（流必须可 Seek，方法返回后流位置复原）。
    /// </summary>
    /// <param name="exeStream">成品 exe 只读流。</param>
    /// <returns>解析出的 zip 偏移与长度。</returns>
    /// <exception cref="InvalidDataException">文件长度不足或魔数 / 偏移量校验失败时抛出。</exception>
    public static PayloadFooter ReadFooter(Stream exeStream)
    {
        ArgumentNullException.ThrowIfNull(exeStream);
        if (!exeStream.CanRead || !exeStream.CanSeek)
        {
            throw new ArgumentException("成品 exe 流必须可读且可 Seek。", nameof(exeStream));
        }

        if (exeStream.Length < PayloadFormat.FooterByteLength)
        {
            throw new InvalidDataException($"文件长度 {exeStream.Length} 字节小于 footer 长度 {PayloadFormat.FooterByteLength} 字节，非 PackKit 成品 exe。");
        }

        long originalPosition = exeStream.Position;
        exeStream.Seek(-PayloadFormat.FooterByteLength, SeekOrigin.End);
        var footerBytes = new byte[PayloadFormat.FooterByteLength];
        exeStream.ReadExactly(footerBytes);
        exeStream.Seek(originalPosition, SeekOrigin.Begin);
        return PayloadFormat.DecodeFooter(footerBytes);
    }

    /// <summary>
    /// 读回成品 exe 内的清单（文件路径版，适用于成品端读取正在运行中的自身 exe）。
    /// </summary>
    /// <param name="exePath">成品 exe 路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结构不变量已兑现的清单。</returns>
    public static async Task<PackManifest> ReadManifestAsync(string exePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        using var exeStream = OpenExeForRead(exePath);
        return await ReadManifestAsync(exeStream, cancellationToken);
    }

    /// <summary>
    /// 读回成品 exe 流内的清单（按 footer 偏移开 zip，读 manifest.json 条目）。
    /// </summary>
    /// <param name="exeStream">成品 exe 只读流（可 Seek）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结构不变量已兑现的清单。</returns>
    /// <exception cref="InvalidDataException">footer 校验失败或 zip 内缺少清单条目时抛出。</exception>
    public static async Task<PackManifest> ReadManifestAsync(Stream exeStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exeStream);
        var footer = ReadFooter(exeStream);

        await using var zipSegment = new SubStream(exeStream, footer.ZipOffset, footer.ZipLength);
        using var archive = new ZipArchive(zipSegment, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = FindEntry(archive, PayloadFormat.ManifestEntryName);
        if (manifestEntry.Length > ManifestJson.MaxManifestJsonLength)
        {
            throw new InvalidDataException($"清单条目解压长度 {manifestEntry.Length} 字节超出上限 {ManifestJson.MaxManifestJsonLength}，拒绝读取。");
        }

        using var entryStream = manifestEntry.Open();
        using var manifestBuffer = new MemoryStream();
        await entryStream.CopyToAsync(manifestBuffer, cancellationToken);
        var manifestJson = Encoding.UTF8.GetString(manifestBuffer.ToArray());
        return ManifestJson.Deserialize(manifestJson);
    }

    /// <summary>
    /// 按清单条目提取单个安装包到目标目录（文件路径版）。
    /// </summary>
    /// <param name="exePath">成品 exe 路径（允许是正在运行中的自身 exe）。</param>
    /// <param name="item">待提取的清单条目（EntryPath / FileName / SizeBytes 用于定位与一致性校验）。</param>
    /// <param name="targetDirectory">提取目标目录（不存在时自动创建）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提取出的目标文件完整路径。</returns>
    public static async Task<string> ExtractItemAsync(string exePath, PackItem item, string targetDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        using var exeStream = OpenExeForRead(exePath);
        return await ExtractItemAsync(exeStream, item, targetDirectory, cancellationToken);
    }

    /// <summary>
    /// 按清单条目提取单个安装包到目标目录（流版）：只解压被请求的条目，非整包解压。
    /// </summary>
    /// <param name="exeStream">成品 exe 只读流（可 Seek）。</param>
    /// <param name="item">待提取的清单条目。</param>
    /// <param name="targetDirectory">提取目标目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提取出的目标文件完整路径。</returns>
    /// <exception cref="InvalidDataException">条目缺失或解压长度与清单 SizeBytes 不一致时抛出。</exception>
    public static async Task<string> ExtractItemAsync(Stream exeStream, PackItem item, string targetDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exeStream);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ValidateItemForExtract(item);

        var footer = ReadFooter(exeStream);
        await using var zipSegment = new SubStream(exeStream, footer.ZipOffset, footer.ZipLength);
        using var archive = new ZipArchive(zipSegment, ZipArchiveMode.Read, leaveOpen: false);
        var entry = FindEntry(archive, item.EntryPath);
        if (entry.Length != item.SizeBytes)
        {
            throw new InvalidDataException($"条目 {item.EntryPath} 解压长度 {entry.Length} 字节与清单 SizeBytes {item.SizeBytes} 不一致，成品可能已损坏。");
        }

        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, item.FileName);
        await using var entryStream = entry.Open();
        await using var targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await entryStream.CopyToAsync(targetStream, CopyBufferSize, cancellationToken);
        return targetPath;
    }

    /// <summary>
    /// 提取前的条目校验：EntryPath 必须符合 packages/{FileName} 契约，FileName 必须为纯文件名。
    /// </summary>
    /// <param name="item">待校验清单条目。</param>
    private static void ValidateItemForExtract(PackItem item)
    {
        var expectedEntryPath = PayloadFormat.BuildPackageEntryPath(item.FileName);
        if (!string.Equals(item.EntryPath, expectedEntryPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"条目 EntryPath「{item.EntryPath}」与契约布局「{expectedEntryPath}」不一致，拒绝提取。");
        }

        if (item.SizeBytes <= 0)
        {
            throw new InvalidDataException($"条目 {item.FileName} 的 SizeBytes {item.SizeBytes} 非法，拒绝提取。");
        }
    }

    /// <summary>
    /// 在 zip 内按条目名查找条目（OrdinalIgnoreCase 匹配，未找到即结构化报错）。
    /// </summary>
    /// <param name="archive">已打开的 zip 归档。</param>
    /// <param name="entryName">条目全名。</param>
    /// <returns>命中的条目。</returns>
    private static ZipArchiveEntry FindEntry(ZipArchive archive, string entryName)
    {
        foreach (var candidate in archive.Entries)
        {
            if (string.Equals(candidate.FullName, entryName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new InvalidDataException($"成品 zip 内未找到条目 {entryName}，成品可能已损坏。");
    }

    /// <summary>
    /// 以宽容共享模式打开 exe 文件（成品端需要读取正在运行中的自身 exe）。
    /// </summary>
    /// <param name="exePath">exe 文件路径。</param>
    /// <returns>只读文件流。</returns>
    private static FileStream OpenExeForRead(string exePath)
    {
        return new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
    }
}
