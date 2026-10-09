using System.Diagnostics;

namespace PackKit.Player.Infrastructure;

/// <summary>
/// 调试输出日志：统一时间戳与级别格式写入调试通道，承载安装流程与异常留痕（无文件落盘）。
/// </summary>
internal static class MemoryLog
{
    /// <summary>
    /// 记录一条常规流程日志。
    /// </summary>
    /// <param name="message">日志内容（不含敏感信息）。</param>
    public static void Info(string message)
    {
        Write("INFO", message, exception: null);
    }

    /// <summary>
    /// 记录一条错误日志（含异常类型与消息）。
    /// </summary>
    /// <param name="message">日志内容（不含敏感信息）。</param>
    /// <param name="exception">关联异常。</param>
    public static void Error(string message, Exception exception)
    {
        Write("ERROR", message, exception);
    }

    /// <summary>
    /// 按统一格式拼装一行日志并写入调试通道。
    /// </summary>
    /// <param name="level">日志级别标记。</param>
    /// <param name="message">日志内容。</param>
    /// <param name="exception">关联异常（可为空）。</param>
    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
        if (exception is not null)
        {
            line += $" | {exception.GetType().Name}: {exception.Message}";
        }

        Debug.WriteLine(line);
    }
}
