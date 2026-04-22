using System.ComponentModel;

namespace UIBlazor.Services.Settings;

public interface IProfileManager : IBaseSettingsProvider
{
    ProfileOptions Current { get; }

    ConnectionProfile ActiveProfile { get; }

    /// <summary>
    /// Событие изменения любого поля в списке профилей <seealso cref="ConnectionProfile"/>
    /// или активного профиля <seealso cref="ProfileOptions"/>
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    Task DeleteProfileAsync(string profileId);

    Task ActivateProfileAsync(string profileId, bool saveImediatly = false);
}
