namespace PackKit.Builder.Models;

/// <summary>
/// 制作端本地设置：记忆各文件对话框最近使用的目录与云备份配置，持久化于数据目录（exe 旁 data\，支持迁移）。
/// </summary>
public sealed class AppSettings
{
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

    /// <summary>云同步备份目录自定义值（空 = 使用默认百度云同步盘资料目录）。</summary>
    public string BackupDirectoryOverride { get; set; } = string.Empty;

    /// <summary>最近一次云同步备份完成时间（null = 从未备份）。</summary>
    public DateTime? LastCloudBackupAt { get; set; }
}
