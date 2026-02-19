namespace UIBlazor.Services.Settings;

public interface IBaseSettingsProvider
{
    Task InitializeAsync();

    Task ResetAsync();
}
