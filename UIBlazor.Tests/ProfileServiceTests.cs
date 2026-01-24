using Moq;
using UIBlazor.Models;
using UIBlazor.Options;
using UIBlazor.Services;

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
        _localStorageMock.Setup(ls => ls.GetItemAsync<List<ConnectionProfile>>("model_profiles"))
            .ReturnsAsync((List<ConnectionProfile>?)null);

        // Act
        var result = await _service.GetProfilesAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal("default", result[0].Name);
        _localStorageMock.Verify(ls => ls.SetItemAsync("model_profiles", It.Is<List<ConnectionProfile>>(l => l.Count == 1)), Times.Once);
    }

    [Fact]
    public async Task GetProfilesAsync_ExistingProfiles_ReturnsProfiles()
    {
        // Arrange
        var profiles = new List<ConnectionProfile> { new() { Name = "test" } };
        _localStorageMock.Setup(ls => ls.GetItemAsync<List<ConnectionProfile>>("model_profiles"))
            .ReturnsAsync(profiles);

        // Act
        var result = await _service.GetProfilesAsync();

        // Assert
        Assert.Equal(profiles, result);
        _localStorageMock.Verify(ls => ls.SetItemAsync(It.IsAny<string>(), It.IsAny<List<ConnectionProfile>>()), Times.Never);
    }

    [Fact]
    public async Task SaveProfileAsync_NewProfile_AddsAndSaves()
    {
        // Arrange
        var profilesInStorage = new List<ConnectionProfile> { new() { Id = "default", Name = "default" } };
        _localStorageMock.Setup(ls => ls.GetItemAsync<List<ConnectionProfile>>("model_profiles"))
            .ReturnsAsync(profilesInStorage);

        var newProfile = new ConnectionProfile { Id = "new", Name = "New Profile" };

        // Act
        await _service.SaveProfileAsync(newProfile);

        // Assert
        _localStorageMock.Verify(ls => ls.SetItemAsync("model_profiles", It.Is<List<ConnectionProfile>>(l => 
            l.Any(p => p.Id == "new"))), Times.Once);
    }

    [Fact]
    public async Task SaveProfileAsync_ExistingProfile_UpdatesAndSaves()
    {
        // Arrange
        var existingProfile = new ConnectionProfile { Id = "existing", Name = "Old Name" };
        var existingProfiles = new List<ConnectionProfile> { existingProfile };
        _localStorageMock.Setup(ls => ls.GetItemAsync<List<ConnectionProfile>>("model_profiles"))
            .ReturnsAsync(existingProfiles);

        var updatedProfile = new ConnectionProfile { Id = "existing", Name = "New Name" };

        // Act
        await _service.SaveProfileAsync(updatedProfile);

        // Assert
        Assert.Equal("New Name", existingProfiles[0].Name);
        _localStorageMock.Verify(ls => ls.SetItemAsync("model_profiles", existingProfiles), Times.Once);
    }

    [Fact]
    public async Task SaveProfileAsync_ActiveProfile_Activates()
    {
        // Arrange
        var existingProfile = new ConnectionProfile { Id = "active", Name = "Active", Endpoint = "http://old" };
        var existingProfiles = new List<ConnectionProfile> { existingProfile };
        _localStorageMock.Setup(ls => ls.GetItemAsync<List<ConnectionProfile>>("model_profiles"))
            .ReturnsAsync(existingProfiles);
        _localStorageMock.Setup(ls => ls.GetItemAsync<string>("active_profile_id"))
            .ReturnsAsync("active");

        var updatedProfile = new ConnectionProfile { Id = "active", Name = "Active", Endpoint = "http://new" };

        // Act
        await _service.SaveProfileAsync(updatedProfile);

        // Assert
        Assert.Equal("http://new", _currentOptions.Endpoint); // Should be updated in options
    }

    [Fact]
    public async Task DeleteProfileAsync_RemovesAndSaves()
    {
        // Arrange
        var profileToDelete = new ConnectionProfile { Id = "delete-me" };
        var existingProfiles = new List<ConnectionProfile> { profileToDelete };
        _localStorageMock.Setup(ls => ls.GetItemAsync<List<ConnectionProfile>>("model_profiles"))
            .ReturnsAsync(existingProfiles);

        // Act
        await _service.DeleteProfileAsync("delete-me");

        // Assert
        Assert.Empty(existingProfiles);
        _localStorageMock.Verify(ls => ls.SetItemAsync("model_profiles", existingProfiles), Times.Once);
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
        var existingProfiles = new List<ConnectionProfile> { profile };
        _localStorageMock.Setup(ls => ls.GetItemAsync<List<ConnectionProfile>>("model_profiles"))
            .ReturnsAsync(existingProfiles);

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

        _localStorageMock.Verify(ls => ls.SetItemAsync("active_profile_id", "test-profile"), Times.Once);
    }
}
