namespace PackKit.Builder.Models;

/// <summary>
/// 制作端本地设置：记忆各文件对话框最近使用的目录，持久化于用户应用数据目录。
/// </summary>
public sealed class AppSettings
{
    /// <summary>最近一次打开或保存工程文件的目录。</summary>
    public string LastProjectDirectory { get; set; } = string.Empty;

    /// <summary>最近一次添加安装包文件的目录。</summary>
    public string LastPackageDirectory { get; set; } = string.Empty;

    /// <summary>最近一次选择文件夹添加安装包的目录。</summary>
    public string LastPackageFolder { get; set; } = string.Empty;

    /// <summary>最近一次选择图标文件的目录。</summary>
    public string LastIconDirectory { get; set; } = string.Empty;

    /// <summary>最近一次选择壳模板文件的目录。</summary>
    public string LastShellDirectory { get; set; } = string.Empty;

    /// <summary>最近一次选择成品输出目录。</summary>
    public string LastOutputDirectory { get; set; } = string.Empty;
}
