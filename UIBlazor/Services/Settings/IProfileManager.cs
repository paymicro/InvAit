namespace UIBlazor.Services.Settings;

public interface IProfileManager : IBaseSettingsProvider, IDisposable
{
    ProfileOptions Current { get; }

    ConnectionProfile ActiveProfile { get; }

    Task DeleteProfileAsync(string profileId);

    Task ActivateProfileAsync(string profileId, bool saveImediatly = false);
}
