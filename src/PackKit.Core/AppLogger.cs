using System.Diagnostics;

namespace PackKit.Core;

/// <summary>
/// 应用文件日志组件：日志固定锚定 C:\simple\PackKit\logs\（固定根跨构建/发布/拷贝位置一致），
/// 按天分文件、保留 7 天、启动时与每日定时自动清理；写日志失败不阻塞主流程，
/// 启动期日志写不进时回落 exe 旁 boot-error.log 兜底留痕。
/// </summary>
public static class AppLogger
{
    /// <summary>日志保留天数：超过该天数的日志文件在清理时删除。</summary>
    private const int RetentionDays = 7;

    /// <summary>每日清理巡检间隔（分钟）：定时器按此周期唤醒，仅在跨天时执行一次清理。</summary>
    private const int CleanupSweepIntervalMinutes = 30;

    /// <summary>日志根目录：固定锚定，不随 exe 位置漂移。</summary>
    private const string LogRoot = @"C:\simple\PackKit\logs";

    /// <summary>
    /// 取日志根目录（供设置页「打开日志目录」等入口消费；目录可能尚未创建）。
    /// </summary>
    /// <returns>日志根目录绝对路径。</returns>
    public static string LogDirectory => LogRoot;

    private static readonly object FileGate = new();

    private static string componentName = string.Empty;
    private static System.Threading.Timer? cleanupTimer;
    private static DateTime lastCleanupDate = DateTime.MinValue;
    private static bool initialized;

    /// <summary>
    /// 初始化日志组件：必须在应用启动最早期调用，禁止延迟到首条业务日志才初始化。
    /// 幂等：重复调用不重建定时器、不重复初始化。
    /// </summary>
    /// <param name="logComponentName">组件名，用于区分日志来源（如 Builder / Player）。</param>
    public static void Initialize(string logComponentName)
    {
        if (initialized)
        {
            return;
        }

        lock (FileGate)
        {
            if (initialized)
            {
                return;
            }

            componentName = string.IsNullOrWhiteSpace(logComponentName) ? "app" : logComponentName.Trim();

            // 组件生命周期兜底内聚：进程退出时释放定时器由组件自身登记，不依赖宿主装配点代注册
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            try
            {
                Directory.CreateDirectory(LogRoot);
                CleanupExpiredLogs();
                cleanupTimer = new System.Threading.Timer(
                    _ => SweepCleanupDaily(),
                    null,
                    TimeSpan.FromMinutes(CleanupSweepIntervalMinutes),
                    TimeSpan.FromMinutes(CleanupSweepIntervalMinutes));
                initialized = true;
                Write("INFO", "日志初始化完成。", exception: null);
            }
            catch (Exception ex)
            {
                // 启动期日志写不进（目录不可建/无权限等）必须落 exe 旁 boot-error.log 兜底，且不阻塞主流程
                WriteBootError($"日志初始化失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 记录一条常规流程日志。
    /// </summary>
    /// <param name="message">日志内容（不得携带口令/密钥/个人信息等敏感数据）。</param>
    public static void Info(string message)
    {
        Write("INFO", message, exception: null);
    }

    /// <summary>
    /// 记录一条警告日志。
    /// </summary>
    /// <param name="message">日志内容（不得携带口令/密钥/个人信息等敏感数据）。</param>
    public static void Warning(string message)
    {
        Write("WARN", message, exception: null);
    }

    /// <summary>
    /// 记录一条错误日志。
    /// </summary>
    /// <param name="message">日志内容（不得携带口令/密钥/个人信息等敏感数据）。</param>
    /// <param name="exception">关联异常（可为空）。</param>
    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    /// <summary>
    /// 进程退出兜底：释放清理定时器（后台线程定时器不阻塞退出，释放只为及时归还资源）。
    /// </summary>
    /// <param name="sender">事件来源。</param>
    /// <param name="e">事件参数。</param>
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        cleanupTimer?.Dispose();
        cleanupTimer = null;
    }

    /// <summary>
    /// 定时巡检：跨入新的一天后执行一次过期日志清理，同一天内重复唤醒不重复清理。
    /// </summary>
    private static void SweepCleanupDaily()
    {
        try
        {
            lock (FileGate)
            {
                if (!initialized || lastCleanupDate.Date == DateTime.Now.Date)
                {
                    return;
                }

                CleanupExpiredLogs();
            }
        }
        catch (Exception ex)
        {
            // 清理失败不影响业务主链路，留痕调试通道便于排查
            Debug.WriteLine($"PackKit 日志清理失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 清理日志目录中超期（超过保留天数）的日志文件。调用方须已持有 FileGate。
    /// </summary>
    private static void CleanupExpiredLogs()
    {
        lastCleanupDate = DateTime.Now;
        if (!Directory.Exists(LogRoot))
        {
            return;
        }

        var expiryThreshold = DateTime.Now.AddDays(-RetentionDays);
        foreach (var logFilePath in Directory.EnumerateFiles(LogRoot, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(logFilePath) < expiryThreshold)
                {
                    File.Delete(logFilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件被占用或无权限时跳过该文件，不中断整体清理
                Debug.WriteLine($"PackKit 过期日志删除跳过：{logFilePath}：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 写入一条日志：追加单行即写即关（低频桌面场景，避免常驻句柄与退出时丢写）；
    /// 写失败不抛出、不阻塞主流程。
    /// </summary>
    /// <param name="level">日志级别标记。</param>
    /// <param name="message">日志内容。</param>
    /// <param name="exception">关联异常（可为空）。</param>
    private static void Write(string level, string message, Exception? exception)
    {
        if (!initialized)
        {
            // 未初始化期间的写入仅留调试通道，启动时序由 Initialize 的调用约定保证
            Debug.WriteLine($"[{level}] {message}");
            return;
        }

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{componentName}] {message}";
        if (exception is not null)
        {
            // 记全量 ToString（含堆栈与内层异常）：仅记 Message 会让空消息异常（如个别运行时抛出的无消息 FileNotFound）无法定位
            line += $" | {exception}";
        }

        try
        {
            lock (FileGate)
            {
                var filePath = Path.Combine(LogRoot, $"{componentName}-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(filePath, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // 运行期写日志失败不阻塞主流程，留痕调试通道；启动期失败已由 Initialize 落 boot-error.log 兜底
            Debug.WriteLine($"PackKit 日志写入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动期兜底：把日志初始化失败原因追加到 exe 旁 boot-error.log，供排障取证；写失败静默放弃。
    /// </summary>
    /// <param name="detail">失败详情。</param>
    private static void WriteBootError(string detail)
    {
        try
        {
            var bootErrorPath = Path.Combine(AppContext.BaseDirectory, "boot-error.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {detail}";
            File.AppendAllText(bootErrorPath, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PackKit boot-error 兜底写入失败：{ex.Message}");
        }
    }
}
