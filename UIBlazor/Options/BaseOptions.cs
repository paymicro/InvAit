using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UIBlazor.Options;

public abstract class BaseOptions : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// При изменении вызывается <see cref="Debouncer"/> в и сохраняет настройки через 750mc
    /// </summary>
    protected void SetIfChanged<T>(ref T storage, T value, [CallerMemberName] string prop = "")
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;

        storage = value;
        RaisePropertyChanged(prop);
    }

    private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
