using UIBlazor.Options;

namespace UIBlazor.Services;

public interface IAiSettingsProvider
{
    AiOptions Current { get; }

    Task InitializeAsync();
    
    Task ResetAsync();
}