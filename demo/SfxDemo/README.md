# SfxDemo —— 单 exe 尾部追加载荷机制实证 demo

验证机制：.NET 8 self-contained 单文件 exe 在文件尾部追加 payload（zip + 32 字节 footer）后，
仍可正常启动，且启动后能读回尾部 footer、解开清单、按条目解压并与源文件字节级比对一致。

## 双击哪个文件

双击 `demo\SfxDemo-packed.exe`（尚未生成时，先用下方脚本构建一次）。

会看到：

1. `[1/4]` footer 校验通过（magic=PACKKITv1）与 zip 偏移/长度；
2. `[2/4]` 清单：标题「演示载荷」与条目名、条目路径、大小；
3. `[3/4]` 逐条目解压到 `%TEMP%\SfxDemo\{Guid}\` 并与 `demo\fixtures\` 源文件字节级比对的逐条结果；
4. `[4/4]` 结论行（footer 偏移 / 清单 N 项 / 提取 N 项全部字节一致）与解压临时目录路径，最后停在「按回车退出」。

双击未打包的壳（`demo\SfxDemo\bin\Release\net8.0\win-x64\publish\SfxDemo.exe`）只会提示：
「我是未打包的空壳：请运行 build-demo.ps1 生成打包版」。

## 重新生成打包版

```
powershell -ExecutionPolicy Bypass -File demo\SfxDemo\build-demo.ps1
```

脚本内完成三步：dotnet publish（self-contained 单文件）→ 本地生成 fixture
（readme.txt 约 2KB 中文文本 + blob.bin 100KB 确定性随机字节）→ 调用壳的 pack 模式
（内部走 PackKit.Core `PayloadWriter.WriteToFileAsync`）→ 产出 `demo\SfxDemo-packed.exe`。

注意：`demo\fixtures\` 是打包版做字节级比对的基准，需与 `SfxDemo-packed.exe` 保持
「exe 在 demo\ 下、fixtures 在 demo\fixtures」的同目录关系；缺失时程序会明确报错并
以退出码 1 结束，不会静默跳过比对。

## 结构一句话图

```
[壳 exe 字节][payload zip 字节（manifest.json + packages/readme.txt + packages/blob.bin）][footer 32 字节 = magic 16B("PACKKITv1"+\0填充) + zipOffset(int64 LE) + zipLength(int64 LE)]
```

## 非交互取证等价性说明

命令行运行 `SfxDemo-packed.exe`（标准输入重定向自 NUL）与双击运行的唯一差异是
`Console.ReadLine` 立即得到 EOF，程序执行路径完全一致，因此命令行捕获的输出与
退出码可作为「双击等价」的自动化取证；本 demo 不引入任何 UI 自动化。
