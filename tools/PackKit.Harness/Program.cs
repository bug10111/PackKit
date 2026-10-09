using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using PackKit.Core;

namespace PackKit.Harness;

/// <summary>
/// 实证探针命令行入口：提供测试夹具生成、成品追加打包、读回校验、单文件 exe 启动取证与安装器类型探测五类命令。
/// </summary>
public static class Program
{
    private const string NoteFileName = "test-note.txt";

    private const string BinaryFileName = "test-payload.bin";

    private const string ManifestFileName = "manifest.json";

    private const int BinaryFileLength = 1024 * 1024;

    /// <summary>
    /// 命令分发入口；各命令以独立退出码区分取证结论（0=通过，非 0=对应失败形态）。
    /// </summary>
    /// <param name="args">命令与参数。</param>
    /// <returns>进程退出码。</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            return args[0] switch
            {
                "makefixture" => await MakeFixtureAsync(args),
                "append" => await AppendAsync(args),
                "readback" => await ReadBackAsync(args),
                "launch" => await LaunchAsync(args),
                "wait" => await WaitAsync(args),
                "detect" => Detect(args),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"HARNESS_FAIL {ex.GetType().Name}: {ex.Message}");
            return 9;
        }
    }

    /// <summary>
    /// makefixture：在目标目录生成说明文件、随机二进制文件与对应清单 JSON。
    /// </summary>
    /// <param name="args">args[1] = 夹具目录。</param>
    /// <returns>进程退出码。</returns>
    private static async Task<int> MakeFixtureAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：makefixture <夹具目录>");
            return 1;
        }

        var fixtureDirectory = args[1];
        Directory.CreateDirectory(fixtureDirectory);

        var notePath = Path.Combine(fixtureDirectory, NoteFileName);
        await File.WriteAllTextAsync(notePath, BuildNoteContent());

        var binaryPath = Path.Combine(fixtureDirectory, BinaryFileName);
        WriteRandomFile(binaryPath, BinaryFileLength);

        var manifest = BuildFixtureManifest(fixtureDirectory);
        var manifestPath = Path.Combine(fixtureDirectory, ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, ManifestJson.Serialize(manifest));

        Console.WriteLine($"FIXTURE_DIR {fixtureDirectory}");
        Console.WriteLine($"NOTE_PATH {notePath}");
        Console.WriteLine($"BINARY_PATH {binaryPath}");
        Console.WriteLine($"MANIFEST_PATH {manifestPath}");
        Console.WriteLine("MANIFEST_JSON " + ManifestJson.Serialize(manifest));
        Console.WriteLine("MAKEFIXTURE_OK");
        return 0;
    }

    /// <summary>
    /// append：读夹具清单并把壳 exe 与夹具内安装包打包为追加版成品 exe。
    /// </summary>
    /// <param name="args">args[1] = 壳 exe 路径；args[2] = 输出成品路径；args[3] = 夹具目录。</param>
    /// <returns>进程退出码。</returns>
    private static async Task<int> AppendAsync(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("用法：append <壳exe路径> <输出成品路径> <夹具目录>");
            return 1;
        }

        var shellExePath = args[1];
        var outputExePath = args[2];
        var fixtureDirectory = args[3];

        var manifestJson = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, ManifestFileName));
        var manifest = ManifestJson.Deserialize(manifestJson);
        var packages = new List<PayloadPackageFile>();
        foreach (var item in manifest.Items)
        {
            var sourcePath = Path.Combine(fixtureDirectory, item.FileName);
            packages.Add(new PayloadPackageFile(item, sourcePath));
        }

        var footer = await PayloadWriter.WriteToFileAsync(shellExePath, manifest, packages, outputExePath);

        var shellLength = new FileInfo(shellExePath).Length;
        var outputLength = new FileInfo(outputExePath).Length;
        Console.WriteLine($"SHELL_BYTES {shellLength}");
        Console.WriteLine($"OUTPUT_BYTES {outputLength}");
        Console.WriteLine($"FOOTER zipOffset={footer.ZipOffset} zipLength={footer.ZipLength}");
        Console.WriteLine("APPEND_OK");
        return 0;
    }

    /// <summary>
    /// readback：从成品 exe 读回 footer 与清单，逐字段与夹具清单比对，并逐条目提取后按字节与源文件比对。
    /// </summary>
    /// <param name="args">args[1] = 成品 exe 路径；args[2] = 提取目录；args[3] = 夹具目录。</param>
    /// <returns>全部比对通过为 0，否则为 2。</returns>
    private static async Task<int> ReadBackAsync(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("用法：readback <成品exe路径> <提取目录> <夹具目录>");
            return 1;
        }

        var exePath = args[1];
        var extractDirectory = args[2];
        var fixtureDirectory = args[3];

        var footer = PayloadReader.ReadFooter(exePath);
        Console.WriteLine($"FOOTER zipOffset={footer.ZipOffset} zipLength={footer.ZipLength}");

        var manifest = await PayloadReader.ReadManifestAsync(exePath);
        Console.WriteLine("MANIFEST_JSON " + ManifestJson.Serialize(manifest));

        var expectedJson = await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, ManifestFileName));
        var expected = ManifestJson.Deserialize(expectedJson);
        bool manifestMatched = CompareManifest(expected, manifest);

        bool allBytesMatched = true;
        foreach (var item in manifest.Items)
        {
            var extractedPath = await PayloadReader.ExtractItemAsync(exePath, item, extractDirectory);
            var sourcePath = Path.Combine(fixtureDirectory, item.FileName);
            var bytesMatched = CompareFiles(sourcePath, extractedPath);
            if (!bytesMatched)
            {
                allBytesMatched = false;
            }

            Console.WriteLine($"EXTRACT {item.FileName} bytesEqual={bytesMatched}");
        }

        Console.WriteLine($"MANIFEST_MATCH {manifestMatched}");
        Console.WriteLine($"ALL_BYTES_MATCH {allBytesMatched}");
        if (manifestMatched && allBytesMatched)
        {
            Console.WriteLine("READBACK_OK");
            return 0;
        }

        Console.WriteLine("READBACK_FAIL");
        return 2;
    }

    /// <summary>
    /// launch：启动目标 exe 并取证其存活指定秒数（存活即算通过），随后结束该测试进程；
    /// seconds 之后可携带透传参数（供追加版 Harness 启动后执行自身子命令，如自读回）；
    /// 透传子命令在存活观察窗口内正常退出（退出码 0）同样判定启动成立（RAN_TO_COMPLETION）。
    /// </summary>
    /// <param name="args">args[1] = 目标 exe 路径；args[2] = 需存活秒数；args[3..] = 透传给目标进程的参数。</param>
    /// <returns>存活或正常退出为 0，异常退出为 3，启动被拒为 4，其它失败为 5。</returns>
    private static async Task<int> LaunchAsync(string[] args)
    {
        if (args.Length < 3 || !double.TryParse(args[2], out var surviveSeconds))
        {
            Console.Error.WriteLine("用法：launch <目标exe路径> <需存活秒数> [透传参数...]");
            return 1;
        }

        var passthroughArgs = args.Skip(3).ToArray();
        Console.WriteLine("ELEVATED_CHECK strategy=windowsPrincipal");
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine($"ELEVATED {IsCurrentProcessElevated()}");
        }
        else
        {
            Console.WriteLine("ELEVATED skipped (non-windows runtime)");
        }

        var startInfo = new ProcessStartInfo(args[1])
        {
            UseShellExecute = false
        };
        foreach (var passthroughArg in passthroughArgs)
        {
            startInfo.ArgumentList.Add(passthroughArg);
        }

        Console.WriteLine($"LAUNCH_TARGET {args[1]} passthrough=[{string.Join(' ', passthroughArgs)}]");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine($"START_DENIED win32Error={ex.NativeErrorCode} message={ex.Message}");
            return 4;
        }

        if (process is null)
        {
            Console.WriteLine("START_NULL");
            return 5;
        }

        var stopwatch = Stopwatch.StartNew();
        var surviveDuration = TimeSpan.FromSeconds(surviveSeconds);
        while (stopwatch.Elapsed < surviveDuration)
        {
            if (process.HasExited)
            {
                if (passthroughArgs.Length > 0 && process.ExitCode == 0)
                {
                    Console.WriteLine($"RAN_TO_COMPLETION afterMs={stopwatch.ElapsedMilliseconds} exitCode={process.ExitCode}");
                    Console.WriteLine("LAUNCH_OK");
                    return 0;
                }

                Console.WriteLine($"EXITED_EARLY exitCode={process.ExitCode} afterMs={stopwatch.ElapsedMilliseconds}");
                return 3;
            }

            await Task.Delay(250);
        }

        Console.WriteLine($"ALIVE afterMs={stopwatch.ElapsedMilliseconds} pid={process.Id}");
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        Console.WriteLine("LAUNCH_OK");
        return 0;
    }

    /// <summary>
    /// wait：空转指定秒数后正常退出（供追加版 Harness 单文件 exe 启动存活取证使用）。
    /// </summary>
    /// <param name="args">args[1] = 空转秒数。</param>
    /// <returns>进程退出码。</returns>
    private static async Task<int> WaitAsync(string[] args)
    {
        if (args.Length < 2 || !double.TryParse(args[1], out var waitSeconds))
        {
            Console.Error.WriteLine("用法：wait <秒数>");
            return 1;
        }

        Console.WriteLine($"WAIT_START {waitSeconds}s");
        await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
        Console.WriteLine("WAIT_DONE");
        return 0;
    }

    /// <summary>
    /// detect：对目标文件执行安装器类型探测并输出默认静默参数。
    /// </summary>
    /// <param name="args">args[1] = 目标文件路径。</param>
    /// <returns>进程退出码。</returns>
    private static int Detect(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：detect <目标文件路径>");
            return 1;
        }

        var detected = InstallerTypeDetector.Detect(args[1]);
        Console.WriteLine($"DETECT {Path.GetFileName(args[1])} -> {detected}");
        Console.WriteLine($"DEFAULT_SILENT_ARGS [{InstallerTypeDetector.GetDefaultSilentArgs(detected)}]");
        return 0;
    }

    /// <summary>
    /// 构造测试夹具清单（两条目：说明文件 unknown 无参数、随机二进制 NSIS /S）。
    /// </summary>
    /// <param name="fixtureDirectory">夹具目录。</param>
    /// <returns>夹具清单。</returns>
    private static PackManifest BuildFixtureManifest(string fixtureDirectory)
    {
        var noteItem = BuildFixtureItem(fixtureDirectory, NoteFileName, "测试说明文件", InstallerType.Unknown, string.Empty);
        var binaryItem = BuildFixtureItem(fixtureDirectory, BinaryFileName, "测试二进制包", InstallerType.Nsis, "/S");
        var items = new List<PackItem> { noteItem, binaryItem };
        var manifest = new PackManifest
        {
            FormatVersion = PayloadFormat.CurrentFormatVersion,
            Title = "PackKit 实证测试包",
            Items = items,
            CreatedAt = DateTimeOffset.Now
        };
        return manifest;
    }

    /// <summary>
    /// 构造单条夹具清单条目（按源文件实长填 SizeBytes，按契约填 EntryPath）。
    /// </summary>
    /// <param name="fixtureDirectory">夹具目录。</param>
    /// <param name="fileName">夹具文件名。</param>
    /// <param name="displayName">展示名称。</param>
    /// <param name="installerType">安装器类型。</param>
    /// <param name="silentArgs">静默参数。</param>
    /// <returns>清单条目。</returns>
    private static PackItem BuildFixtureItem(string fixtureDirectory, string fileName, string displayName, InstallerType installerType, string silentArgs)
    {
        var filePath = Path.Combine(fixtureDirectory, fileName);
        var item = new PackItem
        {
            Id = Guid.NewGuid().ToString(),
            Name = displayName,
            EntryPath = PayloadFormat.BuildPackageEntryPath(fileName),
            FileName = fileName,
            SizeBytes = new FileInfo(filePath).Length,
            InstallerType = installerType,
            SilentArgs = silentArgs
        };
        return item;
    }

    /// <summary>
    /// 生成说明文本内容。
    /// </summary>
    /// <returns>说明文本。</returns>
    private static string BuildNoteContent()
    {
        var lines = new List<string>
        {
            "PackKit 实证测试夹具",
            "本文件用于验证清单读回与按字节提取，内容本身无业务含义。",
            new string('测', 512)
        };
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 写指定长度的随机字节文件。
    /// </summary>
    /// <param name="filePath">目标文件路径。</param>
    /// <param name="length">字节长度。</param>
    private static void WriteRandomFile(string filePath, int length)
    {
        using var targetStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[64 * 1024];
        var random = Random.Shared;
        int remaining = length;
        while (remaining > 0)
        {
            var chunkSize = Math.Min(buffer.Length, remaining);
            random.NextBytes(buffer.AsSpan(0, chunkSize));
            targetStream.Write(buffer, 0, chunkSize);
            remaining -= chunkSize;
        }
    }

    /// <summary>
    /// 逐字段比对两份清单（格式版本、标题、创建时间与全部条目字段）。
    /// </summary>
    /// <param name="expected">夹具期望清单。</param>
    /// <param name="actual">成品读回清单。</param>
    /// <returns>全部字段一致为 true，否则打印 MISMATCH 行并返回 false。</returns>
    private static bool CompareManifest(PackManifest expected, PackManifest actual)
    {
        bool matched = true;
        matched &= CompareField("FormatVersion", expected.FormatVersion.ToString(), actual.FormatVersion.ToString());
        matched &= CompareField("Title", expected.Title, actual.Title);
        matched &= CompareField("CreatedAt", expected.CreatedAt.ToString("O"), actual.CreatedAt.ToString("O"));
        matched &= CompareField("Items.Count", expected.Items.Count.ToString(), actual.Items.Count.ToString());

        for (var index = 0; index < expected.Items.Count && index < actual.Items.Count; index++)
        {
            var expectedItem = expected.Items[index];
            var actualItem = actual.Items[index];
            var prefix = $"Items[{index}]";
            matched &= CompareField($"{prefix}.Id", expectedItem.Id, actualItem.Id);
            matched &= CompareField($"{prefix}.Name", expectedItem.Name, actualItem.Name);
            matched &= CompareField($"{prefix}.EntryPath", expectedItem.EntryPath, actualItem.EntryPath);
            matched &= CompareField($"{prefix}.FileName", expectedItem.FileName, actualItem.FileName);
            matched &= CompareField($"{prefix}.SizeBytes", expectedItem.SizeBytes.ToString(), actualItem.SizeBytes.ToString());
            matched &= CompareField($"{prefix}.InstallerType", expectedItem.InstallerType.ToString(), actualItem.InstallerType.ToString());
            matched &= CompareField($"{prefix}.SilentArgs", expectedItem.SilentArgs, actualItem.SilentArgs);
        }

        return matched;
    }

    /// <summary>
    /// 比对单个字段并在不一致时打印 MISMATCH 行。
    /// </summary>
    /// <param name="fieldName">字段名。</param>
    /// <param name="expected">期望值文本。</param>
    /// <param name="actual">实际值文本。</param>
    /// <returns>一致为 true。</returns>
    private static bool CompareField(string fieldName, string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Console.WriteLine($"FIELD_MATCH {fieldName}");
            return true;
        }

        Console.WriteLine($"FIELD_MISMATCH {fieldName} expected=[{expected}] actual=[{actual}]");
        return false;
    }

    /// <summary>
    /// 按字节比较两个文件（长度一致且逐块内容一致）。
    /// </summary>
    /// <param name="leftPath">左文件路径。</param>
    /// <param name="rightPath">右文件路径。</param>
    /// <returns>字节级一致为 true。</returns>
    private static bool CompareFiles(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (leftInfo.Length != rightInfo.Length)
        {
            Console.WriteLine($"FILE_SIZE_MISMATCH {leftPath}={leftInfo.Length} {rightPath}={rightInfo.Length}");
            return false;
        }

        using var leftStream = leftInfo.OpenRead();
        using var rightStream = rightInfo.OpenRead();
        var leftBuffer = new byte[64 * 1024];
        var rightBuffer = new byte[64 * 1024];
        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 判定当前进程是否以管理员运行（决定追加版成品 exe 启动时是否会弹 UAC）。
    /// </summary>
    /// <returns>当前进程为管理员时为 true。</returns>
    [SupportedOSPlatform("windows")]
    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 打印未知命令提示。
    /// </summary>
    /// <param name="command">未知命令名。</param>
    /// <returns>进程退出码。</returns>
    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintUsage();
        return 1;
    }

    /// <summary>
    /// 打印命令用法。
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine("PackKit.Harness 实证探针");
        Console.WriteLine("  makefixture <夹具目录>                              生成测试夹具（两文件 + 清单）");
        Console.WriteLine("  append <壳exe> <输出成品> <夹具目录>                  壳 exe 追加 payload 产出成品");
        Console.WriteLine("  readback <成品exe> <提取目录> <夹具目录>              读回清单逐字段比对 + 条目按字节比对");
        Console.WriteLine("  launch <目标exe> <需存活秒数> [透传参数...]           启动目标 exe 取证存活（透传参数供追加版自取证）");
        Console.WriteLine("  wait <秒数>                                          空转指定秒数后正常退出");
        Console.WriteLine("  detect <目标文件>                                    安装器类型探测");
    }
}
