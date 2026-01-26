using UIBlazor.Options;

namespace UIBlazor.Services.Settings;

public class CommonSettingsProvider : BaseSettingsProvider<CommonOptions>, ICommonSettingsProvider
{
    public event Action? OnChange;

    public CommonSettingsProvider(ILocalStorageService storage) 
        : base(storage, "CommonSettings")
    {
    }

    protected override void OnAnyPropertyChanged(string? propertyName)
    {
        OnChange?.Invoke();
    }

    protected override void OnInitialized()
    {
        OnChange?.Invoke();
    }

    public override async Task ResetAsync()
    {
        Current.ToolTimeoutMs = 30_000;
        await SaveAsync();
    }
}