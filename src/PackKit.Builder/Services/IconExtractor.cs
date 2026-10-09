using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PackKit.Builder.Services;

/// <summary>
/// 安装包图标抽取器：经 Win32 <c>SHDefExtractIcon</c>（资源管理器同款图标抽取 API）从 exe 的 PE 图标资源
/// 抽取 48×48 图标，转为位图后编码为 base64 PNG 文本。
/// 抽取属展示增强，按清单契约降级：.msi、无图标资源与一切抽取失败场景返回 null（成品端显示统一占位字形），不阻断安装包入列。
/// 选型说明：PrivateExtractIcons 在当前环境实测整体失效（对 notepad.exe 等任意 PE 均返回 0，错误码 15106），
/// SHDefExtractIcon 实测可用且支持显式尺寸，故取该路径（48 档在成品端 20px 行内呈现与高 DPI 缩放下画质稳定）。
/// </summary>
public static class IconExtractor
{
    /// <summary>抽取图标的目标边长（像素）。</summary>
    private const int IconPixelSize = 48;

    /// <summary>
    /// 从 exe 文件抽取首个图标资源并编码为 base64 PNG 文本。
    /// </summary>
    /// <param name="executableFilePath">目标 exe 文件路径。</param>
    /// <returns>抽取成功时为 48×48 PNG 的 base64 文本；.msi、无图标资源或抽取失败时为 null。</returns>
    public static string? ExtractIconPngBase64(string executableFilePath)
    {
        if (string.IsNullOrWhiteSpace(executableFilePath) || !File.Exists(executableFilePath))
        {
            return null;
        }

        // .msi 不是 PE 图标容器，按契约直接降级为无图标
        if (executableFilePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var iconHandle = IntPtr.Zero;
        try
        {
            // nIconSize 编码：LOWORD=大图标边长、HIWORD=小图标边长；此处只要大图标 48×48
            var requestedSize = (uint)IconPixelSize | ((uint)IconPixelSize << 16);
            var extractionResult = SHDefExtractIcon(executableFilePath, 0, 0, out iconHandle, IntPtr.Zero, requestedSize);
            if (extractionResult < 0 || iconHandle == IntPtr.Zero)
            {
                // 文件无图标资源或抽取失败按契约降级为无图标
                return null;
            }

            var iconSource = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            iconSource.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(iconSource));
            using var pngStream = new MemoryStream();
            encoder.Save(pngStream);
            return Convert.ToBase64String(pngStream.ToArray());
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or OutOfMemoryException
                or ExternalException)
        {
            // 图标抽取的失败形态（GDI+/壳层以 OutOfMemoryException 表达损坏图形）按契约降级为无图标，不阻断入列
            return null;
        }
        finally
        {
            // HICON 属 GDI 非托管句柄，位图转换完成后必须显式销毁防泄漏
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(
        string pszIconFile,
        int iIndex,
        uint pwFlags,
        out IntPtr phiconLarge,
        IntPtr phiconSmall,
        uint nIconSize);
}
