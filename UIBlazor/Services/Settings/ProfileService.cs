using System.ComponentModel;
using System.Reflection;
using Microsoft.JSInterop;

namespace UIBlazor.Services.Settings;

public class ProfileService(ILocalStorageService localStorage, ILogger<ProfileService> logger, IJSRuntime jSRuntime)
    : BaseSettingsProvider<ProfileOptions>(localStorage, logger, "ProfileSettings"), IProfileManager
{
    public ConnectionProfile ActiveProfile { get; private set; }

    protected override async Task AfterInitAsync()
    {
        if (Current.Profiles.Count == 0)
        {
            await ResetAsync();
        }
        else
        {
            Current.PropertyChanged += OnPropertyChanged;
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

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileOptions.ActiveProfileId)) {
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
        jSRuntime.InvokeAsync<string>("postVsMessage",
            new VsRequest { Action = BasicEnum.SkipSSL, Payload = skipSsl.ToString() });
    }

    public async Task SaveProfileAsync(ConnectionProfile profile)
    {
        var existing = Current.Profiles.FirstOrDefault(p => p.Id == profile.Id);
        if (existing == null)
        {
            Current.Profiles.Add(profile);
            profile.PropertyChanged += OnProfilePropertyChanged;
        }
        else if (!ReferenceEquals(existing, profile))
        {
            // Copy properties from incoming profile to existing one to maintain references and trigger notifications
            CopyProfileProperties(profile, existing);
        }

        Debouncer.Trigger();

        if (Current.ActiveProfileId == profile.Id)
        {
            await ActivateProfileAsync(Current.ActiveProfileId, saveImediatly: true);
        }
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

    private static void CopyProfileProperties(ConnectionProfile from, ConnectionProfile to)
    {
        var properties = typeof(ConnectionProfile)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true } && p.Name != nameof(ConnectionProfile.Id));

        foreach (var prop in properties)
        {
            var value = prop.GetValue(from);
            prop.SetValue(to, value);
        }
    }
}
