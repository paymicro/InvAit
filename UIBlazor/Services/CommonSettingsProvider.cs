using System.ComponentModel;
using Microsoft.JSInterop;
using Shared.Contracts;
using UIBlazor.Options;

namespace UIBlazor.Services;

public class CommonSettingsProvider : IDisposable
{
    private readonly LocalStorageService _storage;
    private readonly IJSRuntime _jSRuntime;
    private const string _storageKey = "CommonSettings";
    private readonly PeriodicTimer _debounceTimer = new (TimeSpan.FromMilliseconds(750));
    private bool _savePending;
    private readonly CancellationTokenSource _cts = new ();

    public CommonOptions Current { get; private set; } = new ();

    public event Action? OnChange;

    public CommonSettingsProvider(LocalStorageService storage, IJSRuntime jSRuntime)
    {
        _storage = storage;
        _jSRuntime = jSRuntime;
        Current.PropertyChanged += OnAnyPropertyChanged;
        _ = DebounceSaveLoopAsync();
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _savePending = true;
        OnChange?.Invoke();
    }

    private async Task DebounceSaveLoopAsync()
    {
        try
        {
            while (await _debounceTimer.WaitForNextTickAsync(_cts.Token))
            {
                if (_savePending)
                {
                    _savePending = false;
                    await SaveAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SaveAsync()
    {
        await _storage.SetItemAsync(_storageKey, Current);
    }

    public async Task InitializeAsync()
    {
        var saved = await _storage.GetItemAsync<CommonOptions>(_storageKey);
        if (saved != null)
        {
            Current.ToolTimeoutMs = saved.ToolTimeoutMs;
        }
        OnChange?.Invoke();
    }

    public async Task ResetAsync()
    {
        Current.ToolTimeoutMs = 30_000;
        await SaveAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _debounceTimer.Dispose();
        Current.PropertyChanged -= OnAnyPropertyChanged;
    }
}