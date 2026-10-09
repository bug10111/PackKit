namespace PackKit.Core;

/// <summary>
/// 成品 exe 尾部 footer 的解析结果：定位 zip 载荷在成品文件中的字节区间。
/// </summary>
/// <param name="ZipOffset">zip 数据在成品 exe 中的起始偏移（字节，int64 LE 存于 footer 前 16 字节魔数之后）。</param>
/// <param name="ZipLength">zip 数据字节长度（int64 LE 存于 footer 末 8 字节）。</param>
public sealed record PayloadFooter(long ZipOffset, long ZipLength);
