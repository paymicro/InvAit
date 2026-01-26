using UIBlazor.Models;
using UIBlazor.Options;

namespace UIBlazor.Services.Settings;

public interface IProfileManager : IBaseSettingsProvider, IDisposable
{
    ProfileOptions Current { get; }
    ConnectionProfile ActiveProfile { get; }
    Task<List<ConnectionProfile>> GetProfilesAsync();
    Task SaveProfileAsync(ConnectionProfile profile);
    Task DeleteProfileAsync(string profileId);
    Task ActivateProfileAsync(string profileId);
    Task<string?> GetActiveProfileIdAsync();
}
