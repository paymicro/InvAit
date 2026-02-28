using System.Globalization;

namespace UIBlazor.Services.Settings;

public class CommonSettingsProvider(
    ILocalStorageService storage,
    ILogger<CommonSettingsProvider> logger)
    : BaseSettingsProvider<CommonOptions>(storage, logger, "CommonSettings"), ICommonSettingsProvider
{
    protected override Task AfterInitAsync()
    {
        if (Current.Culture.Length != 5)
        {
            // Превратит "en" в "en-US", "ru" в "ru-RU" на основе системных данных
            Current.Culture = CultureInfo.CreateSpecificCulture(Current.Culture).Name;
        }

        return Task.CompletedTask;
    }

    public override async Task ResetAsync()
    {
        Current.ToolTimeoutMs = 30_000;
        await SaveAsync();
    }
}