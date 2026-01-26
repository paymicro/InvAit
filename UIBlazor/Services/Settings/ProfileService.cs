using UIBlazor.Models;
using UIBlazor.Options;

namespace UIBlazor.Services.Settings;

public class ProfileService : BaseSettingsProvider<ProfileOptions>, IProfileManager
{
    private readonly IAiSettingsProvider _aiSettingsProvider;

    public ProfileService(ILocalStorageService localStorage, IAiSettingsProvider aiSettingsProvider)
        : base(localStorage, "ProfileSettings")
    {
        _aiSettingsProvider = aiSettingsProvider;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        if (Current.Profiles.Count == 0)
        {
            await ResetAsync();
        }
    }

    public Task<List<ConnectionProfile>> GetProfilesAsync()
    {
        return Task.FromResult(Current.Profiles);
    }

    public async Task SaveProfileAsync(ConnectionProfile profile)
    {
        var index = Current.Profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            Current.Profiles[index] = profile;
        }
        else
        {
            Current.Profiles.Add(profile);
        }
        
        Debouncer.Trigger();
        
        // If we are saving the currently active profile, we should update the live settings too
        if (Current.ActiveProfileId == profile.Id)
        {
             await ActivateProfileAsync(Current.ActiveProfileId, skipPersistence: true);
        }
    }

    public Task DeleteProfileAsync(string profileId)
    {
        var profile = Current.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            Current.Profiles.Remove(profile);
            Debouncer.Trigger();
        }
        return Task.CompletedTask;
    }

    public Task ActivateProfileAsync(string profileId)
    {
        return ActivateProfileAsync(profileId, skipPersistence: false);
    }

    private async Task ActivateProfileAsync(string profileId, bool skipPersistence)
    {
        var profile = Current.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            // Update current settings
            var current = _aiSettingsProvider.Current;
            current.Endpoint = profile.Endpoint;
            current.ApiKey = profile.ApiKey;
            current.Model = profile.Model;
            current.Temperature = profile.Temperature;
            current.MaxTokens = profile.MaxTokens;
            current.Stream = profile.Stream;
            current.SkipSSL = profile.SkipSSL;
            current.SystemPrompt = profile.SystemPrompt;

            // Save active profile ID
            Current.ActiveProfileId = profileId;
            if (skipPersistence)
            {
                Debouncer.Trigger();
            }
            else
            {
                await SaveAsync();
            }
        }
    }

    public Task<string?> GetActiveProfileIdAsync()
    {
        return Task.FromResult(Current.ActiveProfileId);
    }

    public override Task ResetAsync()
    {
        Current.Profiles = [
            new ConnectionProfile 
            { 
                Name = "default",
                Endpoint = "https://api.openai.com"
            }
        ];
        Current.ActiveProfileId = Current.Profiles[0].Id;
        return SaveAsync();
    }
}