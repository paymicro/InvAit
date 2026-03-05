using System.ComponentModel;
using System.Reflection;

namespace UIBlazor.Services.Settings;

public abstract class BaseSettingsProvider<TOptions> : IBaseSettingsProvider where TOptions : BaseOptions, new()
{
    protected readonly ILocalStorageService Storage;
    protected readonly string StorageKey;
    protected readonly Debouncer Debouncer;
    private bool _isInitializing;
    private readonly ILogger _logger;

    public TOptions Current { get; } = new();

    protected BaseSettingsProvider(
        ILocalStorageService storage,
        ILogger logger,
        string storageKey,
        TimeSpan? debounceDelay = null)
    {
        Storage = storage;
        _logger = logger;
        StorageKey = storageKey;
        Debouncer = new Debouncer(debounceDelay ?? TimeSpan.FromMilliseconds(750), SaveAsync);
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isInitializing)
        {
            OnAnyPropertyChanged(e.PropertyName);
            Debouncer.Trigger();
        }
    }

    /// <summary>
    /// Свойство изменилось и будет сохранено
    /// </summary>
    /// <param name="propertyName">Имя измененного свойства</param>
    protected virtual void OnAnyPropertyChanged(string? propertyName)
    {
    }

    /// <summary>
    /// Событие после сохранения настроек
    /// </summary>
    public event Action? OnSaved;

    /// <summary>
    /// Немедленное сохранение Current объекта.<br/>
    /// Автоматически вызывается при изменении любого свойства, которое использует <see cref="BaseOptions.SetIfChanged"/>
    /// </summary>
    public virtual async Task SaveAsync()
    {
        await Storage.SetItemAsync(StorageKey, Current);
        OnSaved?.Invoke();
    }

    /// <summary>
    /// Загрузка настроек
    /// </summary>
    public async Task InitializeAsync()
    {
        _isInitializing = true;
        try
        {
            var saved = await Storage.GetItemAsync<TOptions>(StorageKey);
            if (saved != null)
            {
                CopyProperties(saved, Current);
            }

            Current.PropertyChanged += OnPropertyChanged;
            await AfterInitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to initialize settings for {StorageKey}: {ex.Message}");
        }
        finally
        {
            _isInitializing = false;
        }
    }

    /// <summary>
    /// Вызывается сразу после загрузки настроек
    /// Сюда надо добавлять первичные манипуляции с настройками
    /// </summary>
    protected virtual Task AfterInitAsync() => Task.CompletedTask;

    protected virtual void CopyProperties(TOptions from, TOptions to)
    {
        var properties = typeof(TOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true });

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
