using Microsoft.JSInterop;
﻿using Shared.Contracts;
﻿using System.ComponentModel;
﻿using UIBlazor.Models;
﻿using UIBlazor.Options;
﻿
﻿namespace UIBlazor.Services.Settings;
﻿
﻿public class ProfileService : BaseSettingsProvider<ProfileOptions>, IProfileManager
﻿{
﻿    private readonly IJSRuntime _jSRuntime;
﻿    private ConnectionProfile? _activeProfile;
﻿
﻿    public ProfileService(ILocalStorageService localStorage, IJSRuntime jSRuntime)
﻿        : base(localStorage, "ProfileSettings")
﻿    {
﻿        _jSRuntime = jSRuntime;
﻿    }
﻿
﻿    public ConnectionProfile ActiveProfile
﻿    {
﻿        get
﻿        {
﻿            if (_activeProfile != null && _activeProfile.Id == Current.ActiveProfileId)
﻿                return _activeProfile;
﻿
﻿            _activeProfile = Current.Profiles.FirstOrDefault(p => p.Id == Current.ActiveProfileId) 
﻿                            ?? Current.Profiles.FirstOrDefault() 
﻿                            ?? new ConnectionProfile { Name = "Default" };
﻿            
﻿            return _activeProfile;
﻿        }
﻿    }
﻿
﻿    public override async Task InitializeAsync()
﻿    {
﻿        await base.InitializeAsync();
﻿
﻿        if (Current.Profiles.Count == 0)
﻿        {
﻿            await ResetAsync();
﻿        }
﻿
﻿        foreach (var profile in Current.Profiles)
﻿        {
﻿            profile.PropertyChanged += OnProfilePropertyChanged;
﻿        }
﻿    }
﻿
﻿    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
﻿    {
﻿        if (sender is ConnectionProfile profile && profile.Id == Current.ActiveProfileId)
﻿        {
﻿            if (e.PropertyName == nameof(ConnectionProfile.SkipSSL))
﻿            {
﻿                NotifySkipSsl(profile.SkipSSL);
﻿            }
﻿        }
﻿        Debouncer.Trigger();
﻿    }
﻿
﻿    private void NotifySkipSsl(bool skipSsl)
﻿    {
﻿        _jSRuntime.InvokeAsync<string>("postVsMessage",
﻿            new VsRequest()
﻿            {
﻿                Action = nameof(ConnectionProfile.SkipSSL),
﻿                Payload = skipSsl.ToString()
﻿            });
﻿    }
﻿
﻿    public Task<List<ConnectionProfile>> GetProfilesAsync()
﻿    {
﻿        return Task.FromResult(Current.Profiles);
﻿    }
﻿
﻿        public async Task SaveProfileAsync(ConnectionProfile profile)
﻿        {
﻿            var existing = Current.Profiles.FirstOrDefault(p => p.Id == profile.Id);
﻿            if (existing == null)
﻿            {
﻿                Current.Profiles.Add(profile);
﻿                profile.PropertyChanged += OnProfilePropertyChanged;
﻿            }
﻿                    else if (!ReferenceEquals(existing, profile))
﻿                    {
﻿                        // Copy properties from incoming profile to existing one to maintain references and trigger notifications
﻿                        CopyProfileProperties(profile, existing);
﻿                    }
﻿            ﻿            Debouncer.Trigger();
﻿            
﻿            if (Current.ActiveProfileId == profile.Id)
﻿            {
﻿                 await ActivateProfileAsync(Current.ActiveProfileId, skipPersistence: true);
﻿            }
﻿        }
﻿    ﻿    public Task DeleteProfileAsync(string profileId)
﻿    {
﻿        var profile = Current.Profiles.FirstOrDefault(p => p.Id == profileId);
﻿        if (profile != null)
﻿        {
﻿            profile.PropertyChanged -= OnProfilePropertyChanged;
﻿            Current.Profiles.Remove(profile);
﻿            Debouncer.Trigger();
﻿        }
﻿        return Task.CompletedTask;
﻿    }
﻿
﻿    public Task ActivateProfileAsync(string profileId)
﻿    {
﻿        return ActivateProfileAsync(profileId, skipPersistence: false);
﻿    }
﻿
﻿    private async Task ActivateProfileAsync(string profileId, bool skipPersistence)
﻿    {
﻿        var profile = Current.Profiles.FirstOrDefault(p => p.Id == profileId);
﻿        if (profile != null)
﻿        {
﻿            Current.ActiveProfileId = profileId;
﻿            _activeProfile = profile;
﻿            
﻿            NotifySkipSsl(profile.SkipSSL);
﻿
﻿            if (skipPersistence)
﻿            {
﻿                Debouncer.Trigger();
﻿            }
﻿            else
﻿            {
﻿                await SaveAsync();
﻿            }
﻿        }
﻿    }
﻿
﻿    public Task<string?> GetActiveProfileIdAsync()
﻿    {
﻿        return Task.FromResult(Current.ActiveProfileId);
﻿    }
﻿
﻿    public override Task ResetAsync()
﻿    {
﻿        foreach (var p in Current.Profiles) p.PropertyChanged -= OnProfilePropertyChanged;
﻿
﻿        var defaultProfile = new ConnectionProfile 
﻿        { 
﻿            Name = "default",
﻿            Endpoint = "https://api.openai.com"
﻿        };
﻿        defaultProfile.PropertyChanged += OnProfilePropertyChanged;
﻿
﻿        Current.Profiles = [defaultProfile];
﻿        Current.ActiveProfileId = defaultProfile.Id;
﻿        _activeProfile = defaultProfile;
﻿
﻿        return SaveAsync();
﻿    }
﻿
﻿        public override void Dispose()
﻿        {
﻿            base.Dispose();
﻿            foreach (var p in Current.Profiles) p.PropertyChanged -= OnProfilePropertyChanged;
﻿        }
﻿    
﻿        private void CopyProfileProperties(ConnectionProfile from, ConnectionProfile to)
﻿        {
﻿            var properties = typeof(ConnectionProfile).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
﻿                .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(ConnectionProfile.Id));
﻿    
﻿            foreach (var prop in properties)
﻿            {
﻿                var value = prop.GetValue(from);
﻿                prop.SetValue(to, value);
﻿            }
﻿        }
﻿    }
﻿    