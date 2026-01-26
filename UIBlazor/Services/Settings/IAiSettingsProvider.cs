using UIBlazor.Options;

namespace UIBlazor.Services.Settings;

public interface IAiSettingsProvider
{
    AiOptions Current { get; }

    Task InitializeAsync();
    
    Task ResetAsync();
}