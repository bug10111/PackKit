using System.Windows;
using System.Windows.Threading;
using PackKit.Builder.Services;

namespace PackKit.Builder;

/// <summary>
/// 制作端应用入口：装配全局资源并承载主窗口生命周期，同时兜底未处理异常避免进程无提示崩溃。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 应用启动：注册全局异常兜底并加载最近使用目录等本地设置。
    /// </summary>
    /// <param name="e">启动事件参数。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppSettingsService.Load();
    }

    /// <summary>
    /// 应用退出：持久化最近使用目录，保证下次启动延续上次的工作位置。
    /// </summary>
    /// <param name="e">退出事件参数。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        AppSettingsService.Save();
        base.OnExit(e);
    }

    /// <summary>
    /// 界面线程未处理异常兜底：向用户呈现可读错误后继续运行，不静默吞异常。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">异常事件参数。</param>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.Windows.MessageBox.Show(
            $"发生未处理错误：{e.Exception.Message}",
            "装机包制作工具",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
