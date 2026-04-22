using System.ComponentModel;
using Microsoft.JSInterop;

namespace UIBlazor.Services.Settings;

public class ProfileService(ILocalStorageService localStorage, ILogger<ProfileService> logger, IJSRuntime jSRuntime)
    : BaseSettingsProvider<ProfileOptions>(localStorage, logger, "ProfileSettings"), IProfileManager
{
    public ConnectionProfile ActiveProfile { get; private set; } = null!;

    protected override async Task AfterInitAsync()
    {
        Current.PropertyChanged += OnPropertyChanged;

        if (Current.Profiles.Count == 0)
        {
            await ResetAsync();
        }

        foreach (var profile in Current.Profiles)
        {
            profile.PropertyChanged += OnPropertyChanged;
        }

        UpdateActiveProfile();
    }

    private void UpdateActiveProfile()
    {
        ActiveProfile = Current.Profiles.FirstOrDefault(p => p.Id == Current.ActiveProfileId) ?? Current.Profiles.First();
        NotifySkipSsl(ActiveProfile.SkipSSL);
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileOptions.ActiveProfileId))
        {
            UpdateActiveProfile();
        }
        else if (e.PropertyName == nameof(ConnectionProfile.SkipSSL) && (sender as ConnectionProfile)?.Id == Current.ActiveProfileId)
        {
            NotifySkipSsl(ActiveProfile.SkipSSL);
        }

        PropertyChanged?.Invoke(sender, e);
        Debouncer.Trigger();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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
        if (profile == null)
            return;

        profile.PropertyChanged -= OnPropertyChanged;
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
            p.PropertyChanged -= OnPropertyChanged;
        }

        var defaultProfile = new ConnectionProfile
        {
            Name = "default",
            Endpoint = "https://api.openai.com"
        };

        defaultProfile.PropertyChanged += OnPropertyChanged;
        Current.Profiles = [defaultProfile];
        Current.ActiveProfileId = defaultProfile.Id;
        ActiveProfile = defaultProfile;

        await SaveAsync();
    }

    public override void Dispose()
    {
        base.Dispose();
        Current.PropertyChanged -= OnPropertyChanged;
        foreach (var p in Current.Profiles)
        {
            p.PropertyChanged -= OnPropertyChanged;
        }
    }
}
