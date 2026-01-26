using Moq;
using UIBlazor.Models;
using UIBlazor.Options;
using UIBlazor.Services.Settings;

namespace UIBlazor.Tests;

public class ProfileServiceTests
{
    private readonly Mock<ILocalStorageService> _localStorageMock;
    private readonly Mock<IAiSettingsProvider> _aiSettingsProviderMock;
    private readonly ProfileService _service;
    private readonly AiOptions _currentOptions;

    public ProfileServiceTests()
    {
        _localStorageMock = new Mock<ILocalStorageService>();
        _aiSettingsProviderMock = new Mock<IAiSettingsProvider>();
        
        _currentOptions = new AiOptions();
        _aiSettingsProviderMock.SetupGet(p => p.Current).Returns(_currentOptions);

        _service = new ProfileService(_localStorageMock.Object, _aiSettingsProviderMock.Object);
    }

    [Fact]
    public async Task GetProfilesAsync_NoProfiles_ReturnsDefaultAndSaves()
    {
        // Arrange
        _localStorageMock.Setup(ls => ls.GetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync((ProfileOptions?)null);
        _localStorageMock.Setup(ls => ls.GetItemAsync<List<ConnectionProfile>>("model_profiles"))
            .ReturnsAsync((List<ConnectionProfile>?)null);

        // Act
        await _service.InitializeAsync();
        var result = await _service.GetProfilesAsync();
        await Task.Delay(800, TestContext.Current.CancellationToken); // Wait for debounce

        // Assert
        Assert.Single(result);
        Assert.Equal("default", result[0].Name);
        _localStorageMock.Verify(ls => ls.SetItemAsync("ProfileSettings", It.Is<ProfileOptions>(o => o.Profiles.Count == 1)), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetProfilesAsync_ExistingProfiles_ReturnsProfiles()
    {
        // Arrange
        var options = new ProfileOptions { Profiles = [new() { Name = "test" }] };
        _localStorageMock.Setup(ls => ls.GetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);

        // Act
        await _service.InitializeAsync();
        var result = await _service.GetProfilesAsync();

        // Assert
        Assert.Equal(options.Profiles, result);
    }

    [Fact]
    public async Task SaveProfileAsync_NewProfile_AddsAndSaves()
    {
        // Arrange
        var options = new ProfileOptions { Profiles = [new() { Id = "default", Name = "default" }] };
        _localStorageMock.Setup(ls => ls.GetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);
        await _service.InitializeAsync();

        var newProfile = new ConnectionProfile { Id = "new", Name = "New Profile" };

        // Act
        await _service.SaveProfileAsync(newProfile);
        await Task.Delay(800, TestContext.Current.CancellationToken); // Wait for debounce

        // Assert
        _localStorageMock.Verify(ls => ls.SetItemAsync("ProfileSettings", It.Is<ProfileOptions>(o => 
            o.Profiles.Any(p => p.Id == "new"))), Times.Once);
    }

    [Fact]
    public async Task SaveProfileAsync_ExistingProfile_UpdatesAndSaves()
    {
        // Arrange
        var existingProfile = new ConnectionProfile { Id = "existing", Name = "Old Name" };
        var options = new ProfileOptions { Profiles = [existingProfile] };
        _localStorageMock.Setup(ls => ls.GetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);
        await _service.InitializeAsync();

        var updatedProfile = new ConnectionProfile { Id = "existing", Name = "New Name" };

        // Act
        await _service.SaveProfileAsync(updatedProfile);
        await Task.Delay(800, TestContext.Current.CancellationToken); // Wait for debounce

        // Assert
        Assert.Equal("New Name", options.Profiles[0].Name);
        _localStorageMock.Verify(ls => ls.SetItemAsync("ProfileSettings", It.IsAny<ProfileOptions>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SaveProfileAsync_ActiveProfile_Activates()
    {
        // Arrange
        var existingProfile = new ConnectionProfile { Id = "active", Name = "Active", Endpoint = "http://old" };
        var options = new ProfileOptions { Profiles = [existingProfile], ActiveProfileId = "active" };
        _localStorageMock.Setup(ls => ls.GetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);
        await _service.InitializeAsync();

        var updatedProfile = new ConnectionProfile { Id = "active", Name = "Active", Endpoint = "http://new" };

        // Act
        await _service.SaveProfileAsync(updatedProfile);
        await Task.Delay(800, TestContext.Current.CancellationToken); // Wait for debounce

        // Assert
        Assert.Equal("http://new", _currentOptions.Endpoint); // Should be updated in options
    }

    [Fact]
    public async Task DeleteProfileAsync_RemovesAndSaves()
    {
        // Arrange
        var profileToDelete = new ConnectionProfile { Id = "delete-me" };
        var options = new ProfileOptions { Profiles = [profileToDelete] };
        _localStorageMock.Setup(ls => ls.GetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);
        await _service.InitializeAsync();

        // Act
        await _service.DeleteProfileAsync("delete-me");
        await Task.Delay(800, TestContext.Current.CancellationToken); // Wait for debounce

        // Assert
        Assert.Empty(options.Profiles);
        _localStorageMock.Verify(ls => ls.SetItemAsync("ProfileSettings", It.IsAny<ProfileOptions>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ActivateProfileAsync_UpdatesOptionsAndSavesId()
    {
        // Arrange
        var profile = new ConnectionProfile 
        { 
            Id = "test-profile",
            Endpoint = "http://test",
            ApiKey = "key",
            Model = "gpt-4",
            Temperature = 0.5,
            MaxTokens = 2000,
            Stream = false,
            SkipSSL = true,
            SystemPrompt = "prompt"
        };
        var options = new ProfileOptions { Profiles = [profile] };
        _localStorageMock.Setup(ls => ls.GetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);
        await _service.InitializeAsync();

        // Act
        await _service.ActivateProfileAsync("test-profile");

        // Assert
        Assert.Equal("http://test", _currentOptions.Endpoint);
        Assert.Equal("key", _currentOptions.ApiKey);
        Assert.Equal("gpt-4", _currentOptions.Model);
        Assert.Equal(0.5, _currentOptions.Temperature);
        Assert.Equal(2000, _currentOptions.MaxTokens);
        Assert.False(_currentOptions.Stream);
        Assert.True(_currentOptions.SkipSSL);
        Assert.Equal("prompt", _currentOptions.SystemPrompt);

        _localStorageMock.Verify(ls => ls.SetItemAsync("ProfileSettings", It.Is<ProfileOptions>(o => o.ActiveProfileId == "test-profile")), Times.Once);
    }
}
