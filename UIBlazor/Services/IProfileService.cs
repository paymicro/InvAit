using UIBlazor.Models;

namespace UIBlazor.Services;

public interface IProfileService : IDisposable
{
    Task<List<ConnectionProfile>> GetProfilesAsync();
    Task SaveProfileAsync(ConnectionProfile profile);
    Task DeleteProfileAsync(string profileId);
    Task ActivateProfileAsync(string profileId);
    Task<string?> GetActiveProfileIdAsync();
}
