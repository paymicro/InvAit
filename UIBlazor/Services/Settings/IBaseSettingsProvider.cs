namespace UIBlazor.Services.Settings;

public interface IBaseSettingsProvider : IDisposable
{
    Task InitializeAsync();

    Task ResetAsync();

    event Action? OnSaved;

    void CallSaveTrigger();
}
