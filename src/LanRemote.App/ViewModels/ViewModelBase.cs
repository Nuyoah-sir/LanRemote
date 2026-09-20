using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanRemote.App.ViewModels;

/// <summary>
/// 最小 ViewModel 基类。
/// </summary>
/// <remarks>
/// ViewModel 不持有裸 Socket，只调用 Session 抽象（06_DEV_STANDARDS.md 第 7 节）；
/// 集合与图像更新必须 marshal 到 Dispatcher。
/// </remarks>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>触发属性变更通知。</summary>
    /// <param name="propertyName">属性名，由编译器自动填充。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>仅在值确实变化时写入并通知。</summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">字段引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名，由编译器自动填充。</param>
    /// <returns>是否发生了变化。</returns>
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
