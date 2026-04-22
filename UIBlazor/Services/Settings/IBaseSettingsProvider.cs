using System.ComponentModel;

namespace UIBlazor.Services.Settings;

public interface IBaseSettingsProvider : IDisposable
{
    Task InitializeAsync();

    /// <summary>
    /// Событие после сохранения настроек
    /// </summary>
    event Action? OnSaved;

    void CallSaveTrigger();
}
