using System.Buffers.Binary;
using System.Text;

namespace PackKit.Core;

/// <summary>
/// 成品 exe 载荷布局常量与编解码：布局为 [壳exe 字节][zip 字节][footer 32 字节]，属冻结契约。
/// </summary>
public static class PayloadFormat
{
    /// <summary>当前清单格式版本号。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>footer 魔数 ASCII 原文（不足 16 字节部分以 \0 补齐）。</summary>
    public const string MagicValue = "PACKKITv1";

    /// <summary>footer 中魔数字段字节长度（含 \0 填充）。</summary>
    public const int MagicByteLength = 16;

    /// <summary>footer 总字节长度 = 魔数 16 字节 + zipOffset(int64 LE) + zipLength(int64 LE)。</summary>
    public const int FooterByteLength = MagicByteLength + sizeof(long) + sizeof(long);

    /// <summary>zip 内清单条目的根路径名。</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>zip 内安装包条目的目录前缀。</summary>
    public const string PackagesFolderName = "packages";

    private static readonly byte[] MagicBytes = CreateMagicBytes();

    /// <summary>
    /// 由安装包纯文件名构造 zip 内条目路径。
    /// </summary>
    /// <param name="fileName">安装包纯文件名（不含路径分隔符）。</param>
    /// <returns>形如 packages/{fileName} 的条目路径。</returns>
    public static string BuildPackageEntryPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return $"{PackagesFolderName}/{fileName}";
    }

    /// <summary>
    /// 编码 32 字节 footer（魔数 + 两个 int64 小端偏移量）。
    /// </summary>
    /// <param name="zipOffset">zip 数据在成品 exe 中的起始偏移（字节）。</param>
    /// <param name="zipLength">zip 数据字节长度。</param>
    /// <returns>长度恒为 <see cref="FooterByteLength"/> 的 footer 字节数组。</returns>
    internal static byte[] EncodeFooter(long zipOffset, long zipLength)
    {
        var footer = new byte[FooterByteLength];
        Encoding.ASCII.GetBytes(MagicValue, footer);
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(MagicByteLength), zipOffset);
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(MagicByteLength + sizeof(long)), zipLength);
        return footer;
    }

    /// <summary>
    /// 解码并校验 footer 字节（魔数不匹配或偏移量为负即判定非 PackKit 成品并拒绝）。
    /// </summary>
    /// <param name="footerBytes">从成品 exe 尾部读出的 footer 原始字节。</param>
    /// <returns>解析出的 zip 偏移与长度。</returns>
    internal static PayloadFooter DecodeFooter(ReadOnlySpan<byte> footerBytes)
    {
        if (footerBytes.Length != FooterByteLength)
        {
            throw new InvalidDataException($"footer 长度 {footerBytes.Length} 字节与契约 {FooterByteLength} 字节不符，非 PackKit 成品 exe。");
        }

        if (!footerBytes.Slice(0, MagicByteLength).SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("footer 魔数校验失败，当前文件不是 PackKit 成品 exe。");
        }

        var zipOffset = BinaryPrimitives.ReadInt64LittleEndian(footerBytes.Slice(MagicByteLength));
        var zipLength = BinaryPrimitives.ReadInt64LittleEndian(footerBytes.Slice(MagicByteLength + sizeof(long)));
        if (zipOffset < 0 || zipLength < 0)
        {
            throw new InvalidDataException($"footer 偏移量非法（zipOffset={zipOffset}, zipLength={zipLength}），成品可能已损坏。");
        }

        return new PayloadFooter(zipOffset, zipLength);
    }

    /// <summary>
    /// 构造补齐 \0 的 16 字节魔数字段。
    /// </summary>
    /// <returns>魔数字节数组。</returns>
    private static byte[] CreateMagicBytes()
    {
        var bytes = new byte[MagicByteLength];
        Encoding.ASCII.GetBytes(MagicValue, bytes);
        return bytes;
    }
}
