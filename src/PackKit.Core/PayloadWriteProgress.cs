namespace PackKit.Core;

/// <summary>
/// 打包进度回调数据：制作端据此呈现打包进度（当前阶段、当前条目字节进度与累计字节）。
/// </summary>
/// <param name="Phase">当前阶段，取值见 <see cref="PhaseShell"/> / <see cref="PhaseManifest"/> / <see cref="PhasePackage"/> / <see cref="PhaseFooter"/>。</param>
/// <param name="CurrentEntryName">当前条目名（清单条目为 manifest.json，安装包条目为 zip 内路径；壳与 footer 阶段为 null）。</param>
/// <param name="CurrentEntryBytesWritten">当前条目已写入字节数。</param>
/// <param name="CurrentEntryTotalBytes">当前条目总字节数（zip 压缩后体积在写入前不可知，故不提供整包总字节）。</param>
/// <param name="TotalBytesWritten">整个成品文件的累计已写入字节数。</param>
public sealed record PayloadWriteProgress(
    string Phase,
    string? CurrentEntryName,
    long CurrentEntryBytesWritten,
    long CurrentEntryTotalBytes,
    long TotalBytesWritten)
{
    /// <summary>阶段值：正在复制壳 exe 字节。</summary>
    public const string PhaseShell = "shell";

    /// <summary>阶段值：正在写入 manifest.json 清单条目。</summary>
    public const string PhaseManifest = "manifest";

    /// <summary>阶段值：正在写入安装包条目（CurrentEntryName 指明当前安装包）。</summary>
    public const string PhasePackage = "package";

    /// <summary>阶段值：正在把产出的 zip 段流式拷入成品文件。</summary>
    public const string PhasePayloadCopy = "payloadCopy";

    /// <summary>阶段值：正在写入尾部 footer。</summary>
    public const string PhaseFooter = "footer";
}
