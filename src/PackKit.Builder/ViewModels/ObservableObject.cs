using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackKit.Builder.ViewModels;

/// <summary>
/// 可观察对象基类：为界面绑定提供属性变更通知。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>属性变更事件。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 触发属性变更通知。
    /// </summary>
    /// <param name="propertyName">属性名（缺省取调用方成员名）。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 设置字段值并在值变化时触发变更通知。
    /// </summary>
    /// <typeparam name="T">字段类型。</typeparam>
    /// <param name="field">字段引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名（缺省取调用方成员名）。</param>
    /// <returns>值发生变化时为 true。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
