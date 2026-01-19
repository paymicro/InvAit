using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UIBlazor.Options;

public abstract class BaseOptions : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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
