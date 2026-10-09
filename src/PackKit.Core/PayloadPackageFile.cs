namespace PackKit.Core;

/// <summary>
/// 打包输入：一条清单条目与其对应的源安装包文件路径的组合。
/// </summary>
/// <param name="Item">清单条目（FileName 决定 zip 条目名，EntryPath 必须与契约布局一致，SizeBytes 必须等于源文件长度）。</param>
/// <param name="SourceFilePath">源安装包在磁盘上的绝对或相对路径（打包时只读）。</param>
public sealed record PayloadPackageFile(PackItem Item, string SourceFilePath);
