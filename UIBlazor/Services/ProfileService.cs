using UIBlazor.Models;

namespace UIBlazor.Services;

public class ProfileService : IProfileService, IDisposable
{
    private readonly ILocalStorageService _localStorage;
    private readonly IAiSettingsProvider _aiSettingsProvider;
    private const string _storageKey = "model_profiles";
    private const string _activeProfileKey = "active_profile_id";

    private readonly PeriodicTimer _debounceTimer = new(TimeSpan.FromMilliseconds(500));
    private bool _savePending;
    private readonly CancellationTokenSource _cts = new();
    private List<ConnectionProfile>? _cachedProfiles;
    private string? _cachedActiveProfileId;

    public ProfileService(ILocalStorageService localStorage, IAiSettingsProvider aiSettingsProvider)
    {
        _localStorage = localStorage;
        _aiSettingsProvider = aiSettingsProvider;
        _ = DebounceSaveLoopAsync();
    }

    private async Task DebounceSaveLoopAsync()
    {
        try
        {
            while (await _debounceTimer.WaitForNextTickAsync(_cts.Token))
            {
                if (_savePending)
                {
                    _savePending = false;
                    await SaveNowAsync();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SaveNowAsync()
    {
        if (_cachedProfiles != null)
        {
            await _localStorage.SetItemAsync(_storageKey, _cachedProfiles);
        }
        if (_cachedActiveProfileId != null)
        {
            await _localStorage.SetItemAsync(_activeProfileKey, _cachedActiveProfileId);
        }
    }

    public async Task<List<ConnectionProfile>> GetProfilesAsync()
    {
        if (_cachedProfiles != null) return _cachedProfiles;

        var profiles = await _localStorage.GetItemAsync<List<ConnectionProfile>>(_storageKey);
        if (profiles == null || profiles.Count == 0)
        {
            profiles = [
                new ConnectionProfile 
                { 
                    Name = "default",
                    Endpoint = "https://api.openai.com"
                }
            ];
            await _localStorage.SetItemAsync(_storageKey, profiles);
        }
        _cachedProfiles = profiles;
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
        
        _savePending = true;
        
        // If we are saving the currently active profile, we should update the live settings too
        var activeId = await GetActiveProfileIdAsync();
        if (activeId == profile.Id)
        {
             await ActivateProfileAsync(activeId, skipPersistence: true);
        }
    }

    public async Task DeleteProfileAsync(string profileId)
    {
        var profiles = await GetProfilesAsync();
        var profile = profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            profiles.Remove(profile);
            _savePending = true;
        }
    }

    public async Task ActivateProfileAsync(string profileId)
    {
        await ActivateProfileAsync(profileId, skipPersistence: false);
    }

    private async Task ActivateProfileAsync(string profileId, bool skipPersistence)
    {
        var profiles = await GetProfilesAsync();
        var profile = profiles.FirstOrDefault(p => p.Id == profileId);
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
            _cachedActiveProfileId = profileId;
            if (skipPersistence)
            {
                _savePending = true;
            }
            else
            {
                await _localStorage.SetItemAsync(_activeProfileKey, profileId);
            }
        }
    }

    public async Task<string?> GetActiveProfileIdAsync()
    {
        if (_cachedActiveProfileId != null) return _cachedActiveProfileId;
        _cachedActiveProfileId = await _localStorage.GetItemAsync<string>(_activeProfileKey);
        return _cachedActiveProfileId;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _debounceTimer.Dispose();
        
        // Final attempt to save if pending
        if (_savePending)
        {
            _ = SaveNowAsync();
        }
    }
}
