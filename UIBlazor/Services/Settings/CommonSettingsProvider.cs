using System.Globalization;

namespace UIBlazor.Services.Settings;

public class CommonSettingsProvider(
    ILocalStorageService storage,
    ILogger<CommonSettingsProvider> logger)
    : BaseSettingsProvider<CommonOptions>(storage, logger, "CommonSettings"), ICommonSettingsProvider
{
    protected override async Task AfterInitAsync()
    {
        if (Current.Culture.Length != 5)
        {
            // Превратит "en" в "en-US", "ru" в "ru-RU" на основе системных данных
            Current.Culture = CultureInfo.CreateSpecificCulture(Current.Culture).Name;
        }

        // Переходный период с 0.0.12 версии. Там было 3 сек и ничего не успевало.
        if (Current.ToolTimeoutMs < 20_000)
        {
            await ResetAsync();
        }
    }

    public override async Task ResetAsync()
    {
        Current.ToolTimeoutMs = 120_000;
        await SaveAsync();
    }
}