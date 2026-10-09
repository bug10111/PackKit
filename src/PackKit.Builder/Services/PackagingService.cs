using System.IO;
using PackKit.Core;

namespace PackKit.Builder.Services;

/// <summary>
/// 打包前置校验失败异常：携带面向用户的可读原因（打包前由界面层展示，不吞异常）。
/// </summary>
public sealed class PackagingValidationException : Exception
{
    /// <summary>
    /// 创建校验失败异常。
    /// </summary>
    /// <param name="message">面向用户的可读原因。</param>
    public PackagingValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 打包请求中的单条安装包输入（与界面行解耦的纯数据）。
/// </summary>
/// <param name="Id">条目唯一标识。</param>
/// <param name="Name">展示名称。</param>
/// <param name="SourceFilePath">源安装包文件路径。</param>
/// <param name="InstallerType">安装器类型。</param>
/// <param name="SilentArgs">静默安装参数。</param>
/// <param name="IconPng">条目图标的 base64 PNG 文本；null 表示无图标。</param>
/// <param name="GroupId">所属分组唯一标识；null 表示未分组。</param>
public sealed record PackagingEntry(string Id, string Name, string SourceFilePath, InstallerType InstallerType, string SilentArgs, string? IconPng, string? GroupId);

/// <summary>
/// 打包请求（纯数据，由界面层组装）。
/// </summary>
/// <param name="Title">成品标题（必填）。</param>
/// <param name="IconPath">可选图标路径；空串表示不嵌入。</param>
/// <param name="ShellExePath">壳模板 exe 路径。</param>
/// <param name="OutputExePath">成品输出路径。</param>
/// <param name="Groups">软件分组集合（顺序即创建顺序；空集合表示未使用分组）。</param>
/// <param name="Entries">安装包条目有序列表。</param>
public sealed record PackagingRequest(string Title, string IconPath, string ShellExePath, string OutputExePath, IReadOnlyList<PackGroup> Groups, IReadOnlyList<PackagingEntry> Entries);

/// <summary>
/// 打包结果：成品输出信息与图标嵌入的处理结论（图标字段保留但当前壳形态下不生效，原因随结果透出给界面）。
/// </summary>
/// <param name="OutputExePath">成品 exe 输出路径。</param>
/// <param name="Footer">写入完成的 footer（zip 偏移与长度）。</param>
/// <param name="IconRequested">用户是否选择了图标。</param>
/// <param name="IconSkipReason">图标未嵌入时的人类可读原因；嵌入成功或未选择图标时为 null。</param>
public sealed record PackagingOutcome(string OutputExePath, PayloadFooter Footer, bool IconRequested, string? IconSkipReason);

/// <summary>
/// 打包服务：校验工程输入 → 组装 PackManifest → 调用 Core 契约写出成品 exe。
/// 图标嵌入经隔离探针实证不可用：对 .NET 单文件自包含壳执行 Win32 资源更新会丢弃尾部 bundle
/// （实证：patch 后文件由 167MB 缩至约 9.9MB，启动报 bundle 损坏；同壳未 patch 仅追加 payload 则启动存活），
/// 因此图标字段保留但不生效，跳过原因随打包结果透出，不静默假实现。
/// </summary>
public static class PackagingService
{
    /// <summary>图标未嵌入的透出原因（实证结论见类型注释）。</summary>
    public const string IconSkipReasonText =
        "图标嵌入暂不可用：单文件自包含壳经资源替换会丢失运行时 bundle（已实证），本次按无图标打包。";

    /// <summary>成品 exe 体积硬上限（字节）：Windows 加载器无法执行达到 2^32 字节的可执行文件（实测 4,294,967,296 字节即报「无法在你的电脑上运行」，2^32−1 为最后合法值）。</summary>
    public const long MaxPackedExeBytes = 4_294_967_295;
    /// <summary>
    /// 校验打包请求并写出成品 exe。
    /// </summary>
    /// <param name="request">打包请求。</param>
    /// <param name="progress">打包进度回调（在发起调用的同步上下文上回调）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>打包结果（含图标处理结论）。</returns>
    /// <exception cref="PackagingValidationException">工程输入未通过前置校验时抛出。</exception>
    public static async Task<PackagingOutcome> PackAsync(
        PackagingRequest request,
        IProgress<PayloadWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var manifest = BuildManifest(request);
        var packages = BuildPackages(manifest, request);
        var footer = await PayloadWriter.WriteToFileAsync(
            request.ShellExePath,
            manifest,
            packages,
            request.OutputExePath,
            progress,
            cancellationToken);

        // 写出后精确兜底：zip 压缩率波动可能使实际体积超出预估，超限成品在任意机器上都无法运行，删除本次产物并以可读错误说明
        if (File.Exists(request.OutputExePath) && new FileInfo(request.OutputExePath).Length > MaxPackedExeBytes)
        {
            File.Delete(request.OutputExePath);
            throw new PackagingValidationException(
                "成品实际体积超过 Windows 单个可执行文件 4GiB 硬上限（超限 exe 无法运行），已删除本次产物。请移除部分安装包（优先移除体积最大的几项）后重新打包。");
        }

        var iconRequested = !string.IsNullOrWhiteSpace(request.IconPath);
        return new PackagingOutcome(request.OutputExePath, footer, iconRequested, iconRequested ? IconSkipReasonText : null);
    }

    /// <summary>
    /// 前置校验：标题必填、条目非空、壳与图标文件存在、每个源文件存在且文件名不冲突（zip 条目同名会互相覆盖）。
    /// </summary>
    /// <param name="request">待校验请求。</param>
    private static void Validate(PackagingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new PackagingValidationException("请先填写成品标题再打包。");
        }

        if (request.Entries.Count == 0)
        {
            throw new PackagingValidationException("请先添加至少一个安装包再打包。");
        }

        if (string.IsNullOrWhiteSpace(request.ShellExePath) || !File.Exists(request.ShellExePath))
        {
            throw new PackagingValidationException($"壳模板文件不存在：{request.ShellExePath}");
        }

        // 成品体积预估硬闸：壳实长 + 各源文件实长为保守上界（zip 压缩只减不增），超限直接拒绝并引导拆分，
        // 避免用户等待完整打包后才在目标机上发现成品无法运行
        var shellBytes = new FileInfo(request.ShellExePath).Length;
        long sourceBytes = 0;
        foreach (var entry in request.Entries)
        {
            sourceBytes += new FileInfo(entry.SourceFilePath).Length;
        }

        if (shellBytes + sourceBytes > MaxPackedExeBytes)
        {
            var estimateGigaBytes = (shellBytes + sourceBytes) / 1073741824.0;
            throw new PackagingValidationException(
                $"预计成品体积 {estimateGigaBytes:F2} GB，超过 Windows 单个可执行文件 4GiB 硬上限" +
                "（超限 exe 在任何电脑上都无法运行）。请把安装包拆分成多个装机包分别打包。");
        }

        if (!string.IsNullOrWhiteSpace(request.IconPath) && !File.Exists(request.IconPath))
        {
            throw new PackagingValidationException($"图标文件不存在：{request.IconPath}");
        }

        foreach (var entry in request.Entries)
        {
            if (!File.Exists(entry.SourceFilePath))
            {
                throw new PackagingValidationException($"安装包源文件不存在（条目「{entry.Name}」）：{entry.SourceFilePath}");
            }
        }

        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in request.Entries)
        {
            var fileName = Path.GetFileName(entry.SourceFilePath);
            if (!seenFileNames.Add(fileName))
            {
                throw new PackagingValidationException($"存在同名安装包文件「{fileName}」，zip 条目会冲突，请重命名源文件后重新添加。");
            }
        }

        // 分组引用完整性校验：条目所属分组必须存在于分组集合，防止悬空引用写入成品清单
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in request.Groups)
        {
            groupIds.Add(group.Id);
        }

        foreach (var entry in request.Entries)
        {
            if (!string.IsNullOrEmpty(entry.GroupId) && !groupIds.Contains(entry.GroupId))
            {
                throw new PackagingValidationException($"安装包「{entry.Name}」所属分组不存在，请重新选择分组后再打包。");
            }
        }
    }

    /// <summary>
    /// 组装清单：条目 EntryPath 与 SizeBytes 严格按 Core 契约生成（EntryPath=packages/{文件名}，SizeBytes=源文件实长），
    /// 分组集合与条目分组引用原样携带。
    /// </summary>
    /// <param name="request">打包请求。</param>
    /// <returns>待写入的清单。</returns>
    private static PackManifest BuildManifest(PackagingRequest request)
    {
        var manifest = new PackManifest { Title = request.Title };
        manifest.Groups.AddRange(request.Groups);
        foreach (var entry in request.Entries)
        {
            var fileName = Path.GetFileName(entry.SourceFilePath);
            var sourceLength = new FileInfo(entry.SourceFilePath).Length;
            manifest.Items.Add(new PackItem
            {
                Id = entry.Id,
                Name = entry.Name,
                EntryPath = PayloadFormat.BuildPackageEntryPath(fileName),
                FileName = fileName,
                SizeBytes = sourceLength,
                InstallerType = entry.InstallerType,
                SilentArgs = entry.SilentArgs,
                IconPng = entry.IconPng,
                GroupId = entry.GroupId
            });
        }

        return manifest;
    }

    /// <summary>
    /// 组装打包输入（清单条目与源文件路径同序一一配对）。
    /// </summary>
    /// <param name="manifest">已组装清单（条目顺序与请求条目顺序一致）。</param>
    /// <param name="request">打包请求。</param>
    /// <returns>打包输入列表。</returns>
    private static List<PayloadPackageFile> BuildPackages(PackManifest manifest, PackagingRequest request)
    {
        var packages = new List<PayloadPackageFile>();
        for (var entryIndex = 0; entryIndex < manifest.Items.Count; entryIndex++)
        {
            packages.Add(new PayloadPackageFile(manifest.Items[entryIndex], request.Entries[entryIndex].SourceFilePath));
        }

        return packages;
    }
}
