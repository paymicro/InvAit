using UIBlazor.Options;

namespace UIBlazor.Services.Settings;

public interface ICommonSettingsProvider
{
    CommonOptions Current { get; }

    Task InitializeAsync();
    
    Task ResetAsync();
}