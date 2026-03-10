using System.ComponentModel;
using Microsoft.JSInterop;

namespace UIBlazor.Services.Settings;

public class ProfileService(ILocalStorageService localStorage, ILogger<ProfileService> logger, IJSRuntime jSRuntime)
    : BaseSettingsProvider<ProfileOptions>(localStorage, logger, "ProfileSettings"), IProfileManager
{
    public ConnectionProfile ActiveProfile { get; private set; } = null!;

    protected override async Task AfterInitAsync()
    {
        if (Current.Profiles.Count == 0)
        {
            await ResetAsync();
        }

        ActiveProfile = Current.Profiles.FirstOrDefault(p => p.Id == Current.ActiveProfileId) ?? Current.Profiles.First();

        foreach (var profile in Current.Profiles)
        {
            profile.PropertyChanged += OnProfilePropertyChanged;
        }

        if (ActiveProfile.SkipSSL)
        {
            NotifySkipSsl(ActiveProfile.SkipSSL);
        }
    }

    protected override void OnAnyPropertyChanged(string? propertyName)
    {
        if (propertyName == nameof(ProfileOptions.ActiveProfileId))
        {
            ActiveProfile = Current.Profiles.FirstOrDefault(p => p.Id == Current.ActiveProfileId) ?? Current.Profiles.First();
        }
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ConnectionProfile profile && profile.Id == Current.ActiveProfileId)
        {
            if (e.PropertyName == nameof(ConnectionProfile.SkipSSL))
            {
                NotifySkipSsl(profile.SkipSSL);
            }
        }

        Debouncer.Trigger();
    }

    private void NotifySkipSsl(bool skipSsl)
    {
        _ = jSRuntime.InvokeAsync<string>("postVsMessage", new VsRequest { Action = BasicEnum.SkipSSL, Payload = skipSsl.ToString() });
    }

    public async Task DeleteProfileAsync(string profileId)
    {
        if (Current.Profiles.Count < 2)
        {
            return; // нельзя удалять единственный профиль.
        }

        var profile = Current.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            profile.PropertyChanged -= OnProfilePropertyChanged;
            Current.Profiles.Remove(profile);

            // актуализация активного профиля
            if (profileId == Current.ActiveProfileId)
            {
                await ActivateProfileAsync(Current.Profiles.First().Id, saveImediatly: true);
            }
            else
            {
                Debouncer.Trigger();
            }
        }

        return;
    }

    public async Task ActivateProfileAsync(string profileId, bool saveImediatly = false)
    {
        var profile = Current.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile != null)
        {
            Current.ActiveProfileId = profileId;

            NotifySkipSsl(profile.SkipSSL);

            if (!saveImediatly)
            {
                Debouncer.Trigger();
            }
            else
            {
                await SaveAsync();
            }
        }
    }

    public override async Task ResetAsync()
    {
        foreach (var p in Current.Profiles)
        {
            p.PropertyChanged -= OnProfilePropertyChanged;
        }

        var defaultProfile = new ConnectionProfile
        {
            Name = "default",
            Endpoint = "https://api.openai.com"
        };

        defaultProfile.PropertyChanged += OnProfilePropertyChanged;
        Current.Profiles = [defaultProfile];
        Current.ActiveProfileId = defaultProfile.Id;
        ActiveProfile = defaultProfile;

        await SaveAsync();
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var p in Current.Profiles)
        {
            p.PropertyChanged -= OnProfilePropertyChanged;
        }
    }
}
