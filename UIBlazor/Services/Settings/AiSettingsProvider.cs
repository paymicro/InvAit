using Microsoft.JSInterop;
using Shared.Contracts;
using UIBlazor.Options;

namespace UIBlazor.Services.Settings;

public class AiSettingsProvider : BaseSettingsProvider<AiOptions>, IAiSettingsProvider
{
    private readonly IJSRuntime _jSRuntime;

    public AiSettingsProvider(ILocalStorageService storage, IJSRuntime jSRuntime) 
        : base(storage, "AiSettings")
    {
        _jSRuntime = jSRuntime;
    }

    protected override void OnAnyPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(AiOptions.SkipSSL))
        {
            // пропуск SSL только на стороне владельца WebView2
            _jSRuntime.InvokeAsync<string>("postVsMessage",
                new VsRequest()
                {
                    Action = nameof(AiOptions.SkipSSL),
                    Payload = Current.SkipSSL.ToString()
                });
        }
    }

    public override async Task ResetAsync()
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
}