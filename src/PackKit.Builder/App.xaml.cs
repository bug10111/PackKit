using System.Windows;
using System.Windows.Threading;
using PackKit.Builder.Services;
using PackKit.Core;

namespace PackKit.Builder;

/// <summary>
/// 制作端应用入口：装配全局资源并承载主窗口生命周期，同时兜底未处理异常避免进程无提示崩溃。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 应用启动：最先完成日志初始化，随后注册全局异常兜底、加载数据目录内设置并启动云备份巡检。
    /// </summary>
    /// <param name="e">启动事件参数。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLogger.Initialize("Builder");
        AppLogger.Info("制作端启动。");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppSettingsService.Load();
        CloudBackupService.StartDailyWatch();
    }

    /// <summary>
    /// 应用退出：持久化最近使用目录，保证下次启动延续上次的工作位置。
    /// </summary>
    /// <param name="e">退出事件参数。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        AppSettingsService.Save();
        AppLogger.Info("制作端退出。");
        base.OnExit(e);
    }

    /// <summary>
    /// 界面线程未处理异常兜底：留痕后向用户呈现可读错误并继续运行，不静默吞异常。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">异常事件参数。</param>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("界面线程出现未处理异常。", e.Exception);
        System.Windows.MessageBox.Show(
            $"发生未处理错误：{e.Exception.Message}",
            "装机包制作工具",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
