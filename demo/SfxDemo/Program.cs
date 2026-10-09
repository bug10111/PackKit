using System.Text;
using PackKit.Core;

namespace SfxDemo;

/// <summary>
/// SfxDemo 演示入口：实证「.NET 8 self-contained 单文件 exe 尾部追加载荷后仍可正常启动，并能把尾部载荷读回解出」。
/// 无参数运行 = 读自身 exe 尾部 footer、解清单、逐条目解压并与源 fixture 字节比对（双击场景）；
/// pack 子命令 = 以 PackKit.Core 真实契约代码把 fixture 打包为成品 exe（供 build-demo.ps1 调用）。
/// </summary>
internal static class Program
{
    /// <summary>打包清单使用的演示标题。</summary>
    private const string DemoManifestTitle = "演示载荷";

    /// <summary>字节比对缓冲区大小（64KB，逐块比对避免整文件读入内存）。</summary>
    private const int CompareBufferSize = 64 * 1024;

    /// <summary>
    /// 程序入口：按调用参数分流到「自身载荷校验」或「打包制作」。
    /// </summary>
    /// <param name="args">
    /// pack 子命令：pack &lt;壳exe路径&gt; &lt;输出exe路径&gt; &lt;fixture目录&gt;；
    /// 其余形态 = 自身载荷校验，可选第一个参数覆盖 fixture 目录（缺省为 exe 同目录的 fixtures 子目录）。
    /// </param>
    /// <returns>进程退出码：0 成功，1 失败。</returns>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool isPackMode = args.Length >= 1 && args[0] == "pack";
        try
        {
            return isPackMode ? await PackAsync(args) : await VerifyAsync(args);
        }
        catch (Exception ex)
        {
            // 任何失败都把完整异常信息打印出来供排查，且保证窗口停留在回车等待（双击场景不闪退、不吞异常）
            Console.WriteLine();
            Console.WriteLine("[失败] 演示过程出现异常：");
            Console.WriteLine(ex.ToString());
            if (!isPackMode)
            {
                PauseBeforeExit();
            }

            return 1;
        }
    }

    /// <summary>
    /// 自身载荷校验：读自身 exe 尾部 footer 校验魔数 → 读回清单 → 逐条目解压到临时目录 → 与源 fixture 字节级比对 → 打印结论。
    /// </summary>
    /// <param name="args">可选第一个参数 = fixture 目录覆盖（缺省为 exe 同目录的 fixtures 子目录）。</param>
    /// <returns>进程退出码：0 全部一致，1 失败。</returns>
    private static async Task<int> VerifyAsync(string[] args)
    {
        Console.WriteLine("== SfxDemo：单 exe 尾部追加载荷机制实证 ==");
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            Console.WriteLine("[失败] 无法取得自身 exe 路径（Environment.ProcessPath 为空），无法继续校验。");
            PauseBeforeExit();
            return 1;
        }

        Console.WriteLine($"自身 exe：{exePath}");

        // 尾部 footer 魔数不匹配说明当前 exe 尚未追加载荷，属空壳形态的预期分支而非错误
        PayloadFooter? footer = TryReadFooter(exePath);
        if (footer is null)
        {
            Console.WriteLine("我是未打包的空壳：请运行 build-demo.ps1 生成打包版");
            PauseBeforeExit();
            return 0;
        }

        Console.WriteLine($"[1/4] footer 校验通过（magic=PACKKITv1）：zip 偏移={footer.ZipOffset} 字节，zip 长度={footer.ZipLength} 字节");

        PackManifest manifest = await PayloadReader.ReadManifestAsync(exePath);
        Console.WriteLine($"[2/4] 清单读取成功：标题「{manifest.Title}」，格式版本 {manifest.FormatVersion}，共 {manifest.Items.Count} 项");
        foreach (var item in manifest.Items)
        {
            Console.WriteLine($"      条目 {item.FileName}（{item.EntryPath}，{item.SizeBytes} 字节）");
        }

        var extractRoot = Path.Combine(Path.GetTempPath(), "SfxDemo", Guid.NewGuid().ToString("N"));
        string? fixtureDirectory = ResolveFixtureDirectory(exePath, args);
        if (fixtureDirectory is null)
        {
            Console.WriteLine("[失败] 未找到 fixture 源文件目录：打包版 exe 同目录下需要 fixtures 子目录（或用第一个参数指定），缺它无法做字节级比对。");
            PauseBeforeExit();
            return 1;
        }

        Console.WriteLine($"[3/4] 逐条目解压到临时目录并与源 fixture 字节比对（fixture 目录：{fixtureDirectory}）");
        int identicalCount = 0;
        foreach (var item in manifest.Items)
        {
            var extractedPath = await PayloadReader.ExtractItemAsync(exePath, item, extractRoot);
            var fixturePath = Path.Combine(fixtureDirectory, item.FileName);
            bool identical = FileEquals(extractedPath, fixturePath);
            Console.WriteLine($"      {item.FileName} → {extractedPath}（比对结果：{(identical ? "字节一致" : "字节不一致")}）");
            if (identical)
            {
                identicalCount++;
            }
        }

        bool allIdentical = identicalCount == manifest.Items.Count;
        Console.WriteLine($"[4/4] 结论：footer 偏移={footer.ZipOffset}，清单标题「{manifest.Title}」共 {manifest.Items.Count} 项，提取 {identicalCount} 项全部字节一致（{(allIdentical ? "机制实证通过" : "存在不一致项")}）");
        Console.WriteLine($"      解压临时目录：{extractRoot}");
        PauseBeforeExit();
        return allIdentical ? 0 : 1;
    }

    /// <summary>
    /// 打包制作：以真实契约 PayloadWriter.WriteToFileAsync 把 fixture 目录内全部文件按清单追加到壳 exe 副本尾部，产出成品 exe。
    /// </summary>
    /// <param name="args">完整参数：pack &lt;壳exe路径&gt; &lt;输出exe路径&gt; &lt;fixture目录&gt;。</param>
    /// <returns>进程退出码：0 成功，1 失败。</returns>
    private static async Task<int> PackAsync(string[] args)
    {
        if (args.Length != 4)
        {
            Console.WriteLine("用法：SfxDemo pack <壳exe路径> <输出exe路径> <fixture目录>");
            return 1;
        }

        var shellExePath = args[1];
        var outputExePath = args[2];
        var fixtureDirectory = args[3];
        if (!File.Exists(shellExePath))
        {
            Console.WriteLine($"[失败] 壳 exe 不存在：{shellExePath}");
            return 1;
        }

        if (!Directory.Exists(fixtureDirectory))
        {
            Console.WriteLine($"[失败] fixture 目录不存在：{fixtureDirectory}");
            return 1;
        }

        // 已带尾部载荷的 exe 不允许再当壳用，避免产出资费层叠嵌套的坏成品
        if (TryReadFooter(shellExePath) is not null)
        {
            Console.WriteLine($"[失败] 壳 exe 已含 PackKit 载荷，拒绝重复打包：{shellExePath}");
            return 1;
        }

        var fixturePaths = Directory.EnumerateFiles(fixtureDirectory, "*", SearchOption.TopDirectoryOnly);
        var orderedFixturePaths = fixturePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        if (orderedFixturePaths.Count == 0)
        {
            Console.WriteLine($"[失败] fixture 目录为空，没有可打包的载荷文件：{fixtureDirectory}");
            return 1;
        }

        var manifest = new PackManifest { Title = DemoManifestTitle, CreatedAt = DateTimeOffset.Now };
        var packages = new List<PayloadPackageFile>();
        foreach (var sourcePath in orderedFixturePaths)
        {
            var fileName = Path.GetFileName(sourcePath);
            var item = new PackItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = fileName,
                EntryPath = PayloadFormat.BuildPackageEntryPath(fileName),
                FileName = fileName,
                SizeBytes = new FileInfo(sourcePath).Length,
            };
            manifest.Items.Add(item);
            packages.Add(new PayloadPackageFile(item, sourcePath));
        }

        PayloadFooter footer = await PayloadWriter.WriteToFileAsync(shellExePath, manifest, packages, outputExePath);
        long outputSize = new FileInfo(outputExePath).Length;
        Console.WriteLine($"打包完成：{outputExePath}");
        Console.WriteLine($"清单「{manifest.Title}」共 {manifest.Items.Count} 项；footer 记录 zip 偏移={footer.ZipOffset} 字节，zip 长度={footer.ZipLength} 字节，成品总大小={outputSize} 字节");
        return 0;
    }

    /// <summary>
    /// 读取 exe 尾部 footer；魔数不匹配或长度不足（非 PackKit 成品）视为空壳形态，返回 null。
    /// </summary>
    /// <param name="exePath">待探测的 exe 路径。</param>
    /// <returns>解析出的 footer；空壳形态返回 null。</returns>
    private static PayloadFooter? TryReadFooter(string exePath)
    {
        try
        {
            return PayloadReader.ReadFooter(exePath);
        }
        catch (InvalidDataException)
        {
            // 非 PackKit 成品 exe 属预期分支：结构化转译为「空壳」语义，不是吞异常
            return null;
        }
    }

    /// <summary>
    /// 解析 fixture 源目录：优先取命令行第一个参数，否则取 exe 同目录的 fixtures 子目录；都不存在返回 null。
    /// </summary>
    /// <param name="exePath">自身 exe 路径。</param>
    /// <param name="args">命令行参数。</param>
    /// <returns>命中的 fixture 目录；未命中返回 null。</returns>
    private static string? ResolveFixtureDirectory(string exePath, string[] args)
    {
        if (args.Length >= 1 && Directory.Exists(args[0]))
        {
            return args[0];
        }

        var exeDirectory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(exeDirectory))
        {
            return null;
        }

        var defaultFixtureDirectory = Path.Combine(exeDirectory, "fixtures");
        return Directory.Exists(defaultFixtureDirectory) ? defaultFixtureDirectory : null;
    }

    /// <summary>
    /// 字节级比较两个文件：长度不同直接判否，否则按缓冲区分块逐字节比对。
    /// </summary>
    /// <param name="leftPath">左文件路径（提取产物）。</param>
    /// <param name="rightPath">右文件路径（源 fixture）。</param>
    /// <returns>全部字节一致为 true。</returns>
    private static bool FileEquals(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (!rightInfo.Exists || leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        var leftBuffer = new byte[CompareBufferSize];
        var rightBuffer = new byte[CompareBufferSize];
        using var leftStream = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var rightStream = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        while (true)
        {
            int leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            if (leftRead == 0)
            {
                return true;
            }

            rightStream.ReadExactly(rightBuffer, 0, leftRead);
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, leftRead)))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 退出前等待回车，保证双击运行时窗口不闪退；标准输入被重定向时 ReadLine 立即得到 EOF 返回，不阻塞自动化取证。
    /// </summary>
    private static void PauseBeforeExit()
    {
        Console.WriteLine("按回车退出…");
        Console.ReadLine();
    }
}
