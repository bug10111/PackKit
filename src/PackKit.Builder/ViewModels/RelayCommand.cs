using System.Windows.Input;

namespace PackKit.Builder.ViewModels;

/// <summary>
/// 通用命令：把界面按钮与 ViewModel 动作关联，并提供可执行态驱动按钮禁用。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> execute;

    private readonly Func<object?, bool>? canExecute;

    /// <summary>
    /// 创建命令。
    /// </summary>
    /// <param name="execute">执行动作。</param>
    /// <param name="canExecute">可执行判定；缺省恒可执行。</param>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    /// <summary>可执行态变化事件（经命令管理器统一刷新）。</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    /// <summary>
    /// 判定命令当前是否可执行。
    /// </summary>
    /// <param name="parameter">命令参数。</param>
    /// <returns>可执行时为 true。</returns>
    bool ICommand.CanExecute(object? parameter)
    {
        return canExecute?.Invoke(parameter) ?? true;
    }

    /// <summary>
    /// 执行命令动作。
    /// </summary>
    /// <param name="parameter">命令参数。</param>
    void ICommand.Execute(object? parameter)
    {
        execute(parameter);
    }
}
