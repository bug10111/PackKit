using System.IO.Compression;
using System.Text;

namespace PackKit.Core;

/// <summary>
/// 成品 exe 打包器：把壳 exe、清单与安装包按冻结契约布局（[壳exe][zip][footer 32 字节]）流式写出为单个成品文件。
/// </summary>
public static class PayloadWriter
{
    private const int CopyBufferSize = 1024 * 1024;

    /// <summary>
    /// 打包写入文件版：读壳 exe 文件流，产出成品 exe 文件（目标目录不存在时自动创建）。
    /// </summary>
    /// <param name="shellExePath">壳 exe 文件路径（成品端发布的单文件程序）。</param>
    /// <param name="manifest">成品清单（Title 必填；各条目 EntryPath / SizeBytes 必须与契约一致）。</param>
    /// <param name="packages">安装包清单条目与源文件路径的组合列表。</param>
    /// <param name="outputExePath">成品 exe 输出路径。</param>
    /// <param name="progress">进度回调，可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入完成的 footer（zip 偏移与长度）。</returns>
    public static async Task<PayloadFooter> WriteToFileAsync(
        string shellExePath,
        PackManifest manifest,
        IReadOnlyList<PayloadPackageFile> packages,
        string outputExePath,
        IProgress<PayloadWriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellExePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputExePath);

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputExePath));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        await using var shellStream = new FileStream(shellExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var outputStream = new FileStream(outputExePath, FileMode.Create, FileAccess.Write, FileShare.None);
        return await WriteAsync(shellStream, manifest, packages, outputStream, progress, cancellationToken);
    }

    /// <summary>
    /// 打包写入流版：把壳 exe 流、清单与安装包流式写出为成品 exe 流（调用方持有并负责关闭两个流）。
    /// zip 段先经独立临时文件产出（框架对非零起始位置的流会把中央目录偏移写成绝对偏移，段内不自洽），
    /// 再流式拷入成品，保证 zip 段自身可按偏移独立打开。
    /// </summary>
    /// <param name="shellExeStream">壳 exe 只读流（必须可 Seek）。</param>
    /// <param name="manifest">成品清单（Title 必填；各条目 EntryPath / SizeBytes 必须与契约一致）。</param>
    /// <param name="packages">安装包清单条目与源文件路径的组合列表。</param>
    /// <param name="outputStream">成品输出流（必须可写、可 Seek）。</param>
    /// <param name="progress">进度回调，可为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入完成的 footer（zip 偏移与长度）。</returns>
    public static async Task<PayloadFooter> WriteAsync(
        Stream shellExeStream,
        PackManifest manifest,
        IReadOnlyList<PayloadPackageFile> packages,
        Stream outputStream,
        IProgress<PayloadWriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shellExeStream);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(outputStream);
        ValidateTargets(shellExeStream, outputStream);
        ValidateManifest(manifest);
        ValidatePackages(manifest, packages);

        var tracker = new ProgressTracker(progress);
        var buffer = new byte[CopyBufferSize];

        // 先原样复制壳 exe 字节，构成成品文件头部
        long shellLength = shellExeStream.Length;
        await CopySegmentAsync(shellExeStream, outputStream, shellLength, buffer,
            PayloadWriteProgress.PhaseShell, null, tracker, accumulateTotalBytes: true, cancellationToken);

        // 壳字节之后紧跟 zip 载荷，起始偏移即当前输出位置
        long zipOffset = outputStream.Position;

        var zipTempPath = Path.Combine(Path.GetTempPath(), $"packkit-payload-{Guid.NewGuid():N}.zip");
        await using (var zipWriteStream = new FileStream(
            zipTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, CopyBufferSize, FileOptions.DeleteOnClose))
        {
            // zip 段独立成文件产出，段内偏移自 0 起，保证读回端按偏移切片后可自洽打开
            long zipLength = await WriteZipSegmentAsync(manifest, packages, zipWriteStream, buffer, tracker, cancellationToken);

            // 读句柄共享模式须覆盖写句柄的 Write 访问，否则与 DeleteOnClose 写句柄冲突
            await using var zipReadStream = new FileStream(zipTempPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
            await CopySegmentAsync(zipReadStream, outputStream, zipLength, buffer,
                PayloadWriteProgress.PhasePayloadCopy, null, tracker, accumulateTotalBytes: true, cancellationToken);

            var footer = PayloadFormat.EncodeFooter(zipOffset, zipLength);
            await outputStream.WriteAsync(footer, cancellationToken);
            await outputStream.FlushAsync(cancellationToken);
            tracker.Add(footer.Length);
            tracker.Report(PayloadWriteProgress.PhaseFooter, null, footer.Length, footer.Length);

            return new PayloadFooter(zipOffset, zipLength);
        }
    }

    /// <summary>
    /// 把清单与安装包写入独立 zip 段（段内偏移自 0 起自洽），返回 zip 字节长度。
    /// </summary>
    /// <param name="manifest">成品清单。</param>
    /// <param name="packages">安装包输入列表。</param>
    /// <param name="zipStream">zip 段写入流。</param>
    /// <param name="buffer">复用缓冲区。</param>
    /// <param name="tracker">进度记录器（条目级进度只透出、不重复累计成品级字节）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>zip 段字节长度。</returns>
    private static async Task<long> WriteZipSegmentAsync(
        PackManifest manifest,
        IReadOnlyList<PayloadPackageFile> packages,
        Stream zipStream,
        byte[] buffer,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true);

        byte[] manifestBytes = Encoding.UTF8.GetBytes(ManifestJson.Serialize(manifest));
        var manifestEntry = archive.CreateEntry(PayloadFormat.ManifestEntryName, CompressionLevel.Optimal);
        await using (var manifestEntryStream = manifestEntry.Open())
        {
            await manifestEntryStream.WriteAsync(manifestBytes, cancellationToken);
        }

        tracker.Report(PayloadWriteProgress.PhaseManifest, PayloadFormat.ManifestEntryName, manifestBytes.Length, manifestBytes.Length);

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryName = PayloadFormat.BuildPackageEntryPath(package.Item.FileName);
            var packageLength = new FileInfo(package.SourceFilePath).Length;
            await using var sourceStream = new FileStream(package.SourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var packageEntry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var entryStream = packageEntry.Open();
            await CopySegmentAsync(sourceStream, entryStream, packageLength, buffer,
                PayloadWriteProgress.PhasePackage, entryName, tracker, accumulateTotalBytes: false, cancellationToken);
        }

        // 归档对象释放时补写 zip 中央目录与 EOCD；显式刷流确保 Length 与读句柄可见内容一致
        archive.Dispose();
        await zipStream.FlushAsync(cancellationToken);
        return zipStream.Length;
    }

    /// <summary>
    /// 校验输入输出流能力：壳流需可 Seek，输出流需可写可 Seek。
    /// </summary>
    /// <param name="shellExeStream">壳 exe 流。</param>
    /// <param name="outputStream">输出流。</param>
    private static void ValidateTargets(Stream shellExeStream, Stream outputStream)
    {
        if (!shellExeStream.CanRead || !shellExeStream.CanSeek)
        {
            throw new ArgumentException("壳 exe 流必须可读且可 Seek。", nameof(shellExeStream));
        }

        if (!outputStream.CanWrite || !outputStream.CanSeek)
        {
            throw new ArgumentException("输出流必须可写且可 Seek。", nameof(outputStream));
        }
    }

    /// <summary>
    /// 校验清单契约：标题必填，格式版本必须为当前契约版本。
    /// </summary>
    /// <param name="manifest">待校验清单。</param>
    private static void ValidateManifest(PackManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Title))
        {
            throw new ArgumentException("清单 Title 不能为空。", nameof(manifest));
        }

        if (manifest.FormatVersion != PayloadFormat.CurrentFormatVersion)
        {
            throw new ArgumentException($"清单格式版本 {manifest.FormatVersion} 与当前契约版本 {PayloadFormat.CurrentFormatVersion} 不符。", nameof(manifest));
        }
    }

    /// <summary>
    /// 逐条校验安装包输入：条目与源文件存在、文件名为纯文件名、EntryPath 与 SizeBytes 与契约一致。
    /// </summary>
    /// <param name="manifest">清单（用于条目归属校验）。</param>
    /// <param name="packages">安装包输入列表。</param>
    private static void ValidatePackages(PackManifest manifest, IReadOnlyList<PayloadPackageFile> packages)
    {
        foreach (var package in packages)
        {
            if (package is null || package.Item is null)
            {
                throw new ArgumentException("安装包输入存在空条目。", nameof(packages));
            }

            if (!BelongsToManifest(manifest, package.Item))
            {
                throw new ArgumentException($"条目 {package.Item.FileName} 不属于传入清单，清单与安装包列表必须同源。", nameof(packages));
            }

            ValidatePlainFileName(package.Item.FileName, nameof(package));
            if (string.IsNullOrWhiteSpace(package.SourceFilePath))
            {
                throw new ArgumentException($"条目 {package.Item.FileName} 的源文件路径为空。", nameof(packages));
            }

            if (!File.Exists(package.SourceFilePath))
            {
                throw new FileNotFoundException($"条目 {package.Item.FileName} 的源文件不存在：{package.SourceFilePath}", package.SourceFilePath);
            }

            var expectedEntryPath = PayloadFormat.BuildPackageEntryPath(package.Item.FileName);
            if (!string.Equals(package.Item.EntryPath, expectedEntryPath, StringComparison.Ordinal))
            {
                throw new ArgumentException($"条目 {package.Item.FileName} 的 EntryPath「{package.Item.EntryPath}」与契约布局「{expectedEntryPath}」不一致。", nameof(packages));
            }

            var sourceLength = new FileInfo(package.SourceFilePath).Length;
            if (package.Item.SizeBytes != sourceLength)
            {
                throw new ArgumentException($"条目 {package.Item.FileName} 的 SizeBytes {package.Item.SizeBytes} 与源文件长度 {sourceLength} 不一致。", nameof(packages));
            }
        }
    }

    /// <summary>
    /// 校验安装包条目属于传入清单（按条目 Id 匹配，容许清单列表本身被拷贝重建）。
    /// </summary>
    /// <param name="manifest">清单。</param>
    /// <param name="item">待校验清单条目。</param>
    /// <returns>清单内存在同 Id 条目时为 true。</returns>
    private static bool BelongsToManifest(PackManifest manifest, PackItem item)
    {
        foreach (var candidate in manifest.Items)
        {
            if (string.Equals(candidate.Id, item.Id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 校验纯文件名：禁止路径分隔符、目录穿越片段与首尾空白。
    /// </summary>
    /// <param name="fileName">待校验文件名。</param>
    /// <param name="paramName">异常参数名。</param>
    private static void ValidatePlainFileName(string fileName, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.AsSpan().Trim().Length != fileName.Length)
        {
            throw new ArgumentException($"文件名「{fileName}」含首尾空白。", paramName);
        }

        var nameOnly = Path.GetFileName(fileName);
        if (!string.Equals(nameOnly, fileName, StringComparison.Ordinal) || fileName.Contains('/') || fileName.Contains('\\'))
        {
            throw new ArgumentException($"文件名「{fileName}」必须为纯文件名，禁止携带路径分隔符。", paramName);
        }

        if (fileName is "." or "..")
        {
            throw new ArgumentException($"文件名「{fileName}」为目录穿越片段，禁止使用。", paramName);
        }
    }

    /// <summary>
    /// 带进度的流式复制：按缓冲分块搬运并在关键节点回调进度，结束时校验搬运字节数与预期一致。
    /// </summary>
    /// <param name="source">源流。</param>
    /// <param name="target">目标流。</param>
    /// <param name="expectedBytes">预期搬运字节数。</param>
    /// <param name="buffer">复用缓冲区。</param>
    /// <param name="phase">进度阶段名。</param>
    /// <param name="entryName">当前条目名（无条目阶段为 null）。</param>
    /// <param name="tracker">进度记录器。</param>
    /// <param name="accumulateTotalBytes">是否把本段字节计入成品级累计（zip 段中间产物不计入，避免重复统计）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task CopySegmentAsync(
        Stream source,
        Stream target,
        long expectedBytes,
        byte[] buffer,
        string phase,
        string? entryName,
        ProgressTracker tracker,
        bool accumulateTotalBytes,
        CancellationToken cancellationToken)
    {
        long entryBytesWritten = 0;
        tracker.Report(phase, entryName, 0, expectedBytes);
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            entryBytesWritten += read;
            if (accumulateTotalBytes)
            {
                tracker.Add(read);
            }

            tracker.Report(phase, entryName, entryBytesWritten, expectedBytes);
        }

        if (entryBytesWritten != expectedBytes)
        {
            throw new InvalidDataException($"源数据搬运长度与预期不一致：预期 {expectedBytes} 字节，实际 {entryBytesWritten} 字节。");
        }
    }

    /// <summary>
    /// 打包进度累计记录器：维护成品级累计字节并向回调方透出结构化进度。
    /// </summary>
    private sealed class ProgressTracker
    {
        private readonly IProgress<PayloadWriteProgress>? progress;

        private long totalBytesWritten;

        /// <summary>
        /// 创建进度记录器。
        /// </summary>
        /// <param name="progress">进度回调，可为 null。</param>
        public ProgressTracker(IProgress<PayloadWriteProgress>? progress)
        {
            this.progress = progress;
        }

        /// <summary>
        /// 累计已写入字节数。
        /// </summary>
        /// <param name="byteCount">本段写入字节数。</param>
        public void Add(long byteCount)
        {
            totalBytesWritten += byteCount;
        }

        /// <summary>
        /// 上报一次结构化进度。
        /// </summary>
        /// <param name="phase">当前阶段名。</param>
        /// <param name="entryName">当前条目名。</param>
        /// <param name="entryBytesWritten">当前条目已写字节数。</param>
        /// <param name="entryTotalBytes">当前条目总字节数。</param>
        public void Report(string phase, string? entryName, long entryBytesWritten, long entryTotalBytes)
        {
            progress?.Report(new PayloadWriteProgress(phase, entryName, entryBytesWritten, entryTotalBytes, totalBytesWritten));
        }
    }
}
