namespace UIBlazor.Services.Settings;

public class CommonSettingsProvider(ILocalStorageService storage)
    : BaseSettingsProvider<CommonOptions>(storage, "CommonSettings"), ICommonSettingsProvider
{
    public override async Task ResetAsync()
    {
        Current.ToolTimeoutMs = 30_000;
        await SaveAsync();
    }
}