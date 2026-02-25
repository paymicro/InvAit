namespace UIBlazor.Services.Settings;

public class CommonSettingsProvider(
    ILocalStorageService storage,
    ILogger<CommonSettingsProvider> logger)
    : BaseSettingsProvider<CommonOptions>(storage, logger, "CommonSettings"), ICommonSettingsProvider
{
    public override async Task ResetAsync()
    {
        Current.ToolTimeoutMs = 30_000;
        await SaveAsync();
    }
}