using System.ComponentModel;
using System.Reflection;
using UIBlazor.Options;
using UIBlazor.Utils;

namespace UIBlazor.Services.Settings;

public abstract class BaseSettingsProvider<TOptions> : IBaseSettingsProvider, IDisposable where TOptions : BaseOptions, new()
{
    protected readonly ILocalStorageService Storage;
    protected readonly string StorageKey;
    protected readonly Debouncer Debouncer;
    private bool _isInitializing;

    public TOptions Current { get; } = new();

    protected BaseSettingsProvider(ILocalStorageService storage, string storageKey, TimeSpan? debounceDelay = null)
    {
        Storage = storage;
        StorageKey = storageKey;
        Debouncer = new Debouncer(debounceDelay ?? TimeSpan.FromMilliseconds(750), SaveAsync);
        Current.PropertyChanged += OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isInitializing)
        {
            OnAnyPropertyChanged(e.PropertyName);
            Debouncer.Trigger();
        }
    }

    protected virtual void OnAnyPropertyChanged(string? propertyName)
    {
    }

    public virtual async Task SaveAsync()
    {
        await Storage.SetItemAsync(StorageKey, Current);
    }

    public virtual async Task InitializeAsync()
    {
        _isInitializing = true;
        try
        {
            var saved = await Storage.GetItemAsync<TOptions>(StorageKey);
            if (saved != null)
            {
                CopyProperties(saved, Current);
            }
            OnInitialized();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize settings for {StorageKey}: {ex.Message}");
        }
        finally
        {
            _isInitializing = false;
        }
    }

    protected virtual void OnInitialized()
    {
    }

    protected virtual void CopyProperties(TOptions from, TOptions to)
    {
        var properties = typeof(TOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

        foreach (var prop in properties)
        {
            var value = prop.GetValue(from);
            prop.SetValue(to, value);
        }
    }

    public abstract Task ResetAsync();

    public virtual void Dispose()
    {
        Current.PropertyChanged -= OnPropertyChanged;
        Debouncer.Dispose();
    }
}
