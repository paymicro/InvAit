using System.ComponentModel;
using Microsoft.JSInterop;
using Shared.Contracts;
using UIBlazor.Options;

namespace UIBlazor.Services;

public class AiSettingsProvider : IDisposable
{
    private readonly LocalStorageService _storage;
    private readonly IJSRuntime _jSRuntime;
    private const string _storageKey = "AiSettings";
    private readonly PeriodicTimer _debounceTimer = new (TimeSpan.FromMilliseconds(750));
    private bool _savePending;
    private readonly CancellationTokenSource _cts = new ();

    public AiOptions Current { get; private set; } = new ();

    public event Action? OnChange;

    public AiSettingsProvider(LocalStorageService storage, IJSRuntime jSRuntime)
    {
        _storage = storage;
        _jSRuntime = jSRuntime;
        Current.PropertyChanged += OnAnyPropertyChanged;
        _ = DebounceSaveLoopAsync();
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _savePending = true;
        
        if (e.PropertyName == nameof(AiOptions.SkipSSL))
        {
            // пропуск SSL только на стороне владельца WebView2
            _jSRuntime.InvokeAsync<string>("postVsMessage",
                new VsRequest()
                {
                    Action = nameof(AiOptions.SkipSSL),
                    Payload = Current.SkipSSL.ToString()
                });
        }

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
        var saved = await _storage.GetItemAsync<AiOptions>(_storageKey);
        if (saved != null)
        {
            Current.ApiKey = saved.ApiKey;
            Current.ApiKeyHeader = saved.ApiKeyHeader;
            Current.AvailableModels = saved.AvailableModels ?? [];
            Current.Endpoint = saved.Endpoint;
            Current.MaxMessages = saved.MaxMessages;
            Current.MaxTokens = saved.MaxTokens;
            Current.Model = saved.Model;
            Current.Proxy = saved.Proxy;
            Current.SessionMaxAgeHours = saved.SessionMaxAgeHours;
            Current.Stream = saved.Stream;
            Current.SkipSSL = saved.SkipSSL;
            Current.SystemPrompt = saved.SystemPrompt;
            Current.Temperature = saved.Temperature;
        }
        OnChange?.Invoke();
    }

    public async Task ResetAsync()
    {
        Current.ApiKeyHeader = "Authorization";
        Current.MaxMessages = 50;
        Current.MaxTokens = 100000;
        Current.SessionMaxAgeHours = 24;
        Current.Stream = true;
        Current.SkipSSL = false;
        Current.SystemPrompt = "You are a helpful AI code assistant.";
        Current.Temperature = 0.7;
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