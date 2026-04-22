using System.ComponentModel;
using System.Reflection;

namespace UIBlazor.Services.Settings;

public abstract class BaseSettingsProvider<TOptions> : IBaseSettingsProvider where TOptions : BaseOptions, new()
{
    private readonly ILocalStorageService _storage;
    private readonly string _storageKey;
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
        _storage = storage;
        _logger = logger;
        _storageKey = storageKey;
        Debouncer = new Debouncer(debounceDelay ?? TimeSpan.FromMilliseconds(750), SaveAsync);
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        Debouncer.Trigger();
    }

    public void CallSaveTrigger()
    {
        if (_isInitializing)
            return;
        Debouncer.Trigger();
    }

    public event Action? OnSaved;

    /// <summary>
    /// Немедленное сохранение Current объекта.<br/>
    /// Автоматически вызывается при изменении любого свойства, которое использует <see cref="BaseOptions.SetIfChanged"/>
    /// </summary>
    public virtual async Task SaveAsync()
    {
        await _storage.SetItemAsync(_storageKey, Current);
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
            var saved = await _storage.TryGetItemAsync<TOptions>(_storageKey);
            if (saved != null)
            {
                CopyProperties(saved, Current);
            }

            Current.PropertyChanged += OnPropertyChanged;
            await AfterInitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to initialize settings for {_storageKey}: {ex.Message}");
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

    private void CopyProperties(TOptions from, TOptions to)
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
