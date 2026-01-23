using UIBlazor.Options;

namespace UIBlazor.Services;

public interface ICommonSettingsProvider
{
    CommonOptions Current { get; }

    Task InitializeAsync();
    
    Task ResetAsync();
}