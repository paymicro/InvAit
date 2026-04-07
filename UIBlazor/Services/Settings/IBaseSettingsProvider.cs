namespace UIBlazor.Services.Settings;

public interface IBaseSettingsProvider : IDisposable
{
    Task InitializeAsync();

    event Action? OnSaved;

    void CallSaveTrigger();
}
