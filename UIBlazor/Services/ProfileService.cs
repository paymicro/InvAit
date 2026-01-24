using UIBlazor.Models;

namespace UIBlazor.Services;

public class ProfileService(ILocalStorageService localStorage, IAiSettingsProvider aiSettingsProvider) : IProfileService
{
    private const string _storageKey = "model_profiles";
    private const string _activeProfileKey = "active_profile_id";

    public async Task<List<ConnectionProfile>> GetProfilesAsync()
    {
        var profiles = await localStorage.GetItemAsync<List<ConnectionProfile>>(_storageKey);
        if (profiles == null || profiles.Count == 0)
        {
            profiles = [
                new ConnectionProfile 
                { 
                    Name = "default",
                    Endpoint = "https://api.openai.com"
                }
            ];
            await localStorage.SetItemAsync(_storageKey, profiles);
        }
        return profiles;
    }

    public async Task SaveProfileAsync(ConnectionProfile profile)
    {
        var profiles = await GetProfilesAsync();
        var index = profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            profiles[index] = profile;
        }
        else
        {
            profiles.Add(profile);
        }
        await localStorage.SetItemAsync(_storageKey, profiles);
        
        // If we are saving the currently active profile, we should update the live settings too
        var activeId = await GetActiveProfileIdAsync();
        if (activeId == profile.Id)
        {
             await ActivateProfileAsync(activeId);
        }
    }

    public async Task DeleteProfileAsync(string profileId)
    {
        var profiles = await GetProfilesAsync();
        var profile = profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            profiles.Remove(profile);
            await localStorage.SetItemAsync(_storageKey, profiles);
        }
    }

    public async Task ActivateProfileAsync(string profileId)
    {
        var profiles = await GetProfilesAsync();
        var profile = profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            // Update current settings
            var current = aiSettingsProvider.Current;
            current.Endpoint = profile.Endpoint;
            current.ApiKey = profile.ApiKey;
            current.Model = profile.Model;
            current.Temperature = profile.Temperature;
            current.MaxTokens = profile.MaxTokens;
            current.Stream = profile.Stream;
            current.SkipSSL = profile.SkipSSL;
            current.SystemPrompt = profile.SystemPrompt;

            // Save active profile ID
            await localStorage.SetItemAsync(_activeProfileKey, profileId);
        }
    }

    public async Task<string?> GetActiveProfileIdAsync()
    {
        return await localStorage.GetItemAsync<string>(_activeProfileKey);
    }
}
