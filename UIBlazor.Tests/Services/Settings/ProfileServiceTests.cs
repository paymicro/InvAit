namespace UIBlazor.Tests.Services.Settings;

public class ProfileServiceTests
{
    private readonly Mock<ILocalStorageService> _localStorageMock;
    private readonly Mock<IJSRuntime> _jsRuntimeMock;
    private readonly ProfileService _service;
    private readonly ILogger<ProfileService> _logger;

    public ProfileServiceTests()
    {
        _localStorageMock = new Mock<ILocalStorageService>();
        _jsRuntimeMock = new Mock<IJSRuntime>();
        _logger = new LoggerMock<ProfileService>();

        _service = new ProfileService(_localStorageMock.Object, _logger, _jsRuntimeMock.Object);
    }

    [Fact]
    public async Task GetProfilesAsync_NoProfiles_ReturnsDefaultAndSaves()
    {
        // Arrange
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync((ProfileOptions?)null);
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<List<ConnectionProfile>>("model_profiles"))
            .ReturnsAsync((List<ConnectionProfile>?)null);

        // Act
        await _service.InitializeAsync();
        var result = _service.Current.Profiles;
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
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);

        // Act
        await _service.InitializeAsync();
        var result = _service.Current.Profiles;

        // Assert
        Assert.Equal(options.Profiles, result);
    }

    [Fact]
    public async Task DeleteProfileAsync_RemovesAndSaves_ShouldNotDeleteProfile()
    {
        // Arrange
        var profileToDelete = new ConnectionProfile { Id = "delete-me" };
        var options = new ProfileOptions { Profiles = [profileToDelete] };
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);
        await _service.InitializeAsync();

        // Act
        await _service.DeleteProfileAsync(profileToDelete.Id);
        await Task.Delay(800, TestContext.Current.CancellationToken); // Wait for debounce

        // Assert
        Assert.Single(options.Profiles);
        _localStorageMock.Verify(ls => ls.SetItemAsync("ProfileSettings", It.IsAny<ProfileOptions>()), Times.Never);
    }

    [Fact]
    public async Task DeleteProfileAsync_RemovesAndSaves()
    {
        // Arrange
        var profileFirst = new ConnectionProfile { Id = "first" };
        var profileToDelete = new ConnectionProfile { Id = "delete-me" };
        var options = new ProfileOptions { Profiles = [profileFirst, profileToDelete] };
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);
        await _service.InitializeAsync();

        // Act
        await _service.DeleteProfileAsync(profileToDelete.Id);
        await Task.Delay(800, TestContext.Current.CancellationToken); // Wait for debounce

        // Assert
        Assert.Single(options.Profiles);
        _localStorageMock.Verify(ls => ls.SetItemAsync("ProfileSettings", It.IsAny<ProfileOptions>()), Times.Once);
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
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);
        await _service.InitializeAsync();

        // Act
        await _service.ActivateProfileAsync("test-profile", true);

        // Assert
        Assert.Equal("http://test", _service.ActiveProfile.Endpoint);
        Assert.Equal("key", _service.ActiveProfile.ApiKey);
        Assert.Equal("gpt-4", _service.ActiveProfile.Model);
        Assert.Equal(0.5, _service.ActiveProfile.Temperature);
        Assert.Equal(2000, _service.ActiveProfile.MaxTokens);
        Assert.False(_service.ActiveProfile.Stream);
        Assert.True(_service.ActiveProfile.SkipSSL);
        Assert.Equal("prompt", _service.ActiveProfile.SystemPrompt);

        _localStorageMock.Verify(ls => ls.SetItemAsync("ProfileSettings", It.Is<ProfileOptions>(o => o.ActiveProfileId == "test-profile")), Times.Once);
    }

    [Fact]
    public async Task PropertyChanged_SkipSSL_InvokesJs()
    {
        // Arrange
        var profile = new ConnectionProfile { Id = "active", SkipSSL = false };
        var options = new ProfileOptions { Profiles = [profile], ActiveProfileId = "active" };
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ProfileOptions>("ProfileSettings"))
            .ReturnsAsync(options);
        await _service.InitializeAsync();

        // Act
        _service.ActiveProfile.SkipSSL = true;

        // Assert
        _jsRuntimeMock.Verify(js => js.InvokeAsync<string>("postVsMessage", It.Is<object[]>(args =>
            CheckSkipSslRequest(args)
        )), Times.Once);
    }

    private static bool CheckSkipSslRequest(object[] args)
    {
        if (args.Length != 1) return false;
        if (args[0] is not VsRequest req) return false;
        return req.Action == BasicEnum.SkipSSL && req.Payload == "True";
    }

    [Fact]
    public async Task ResetAsync_ResetsToDefaultsAndSaves()
    {
        // Act
        await _service.ResetAsync();

        // Assert
        Assert.Single(_service.Current.Profiles);
        Assert.Equal(0.7, _service.ActiveProfile.Temperature);
        Assert.Equal(string.Empty, _service.ActiveProfile.SystemPrompt);
        _localStorageMock.Verify(ls => ls.SetItemAsync("ProfileSettings", _service.Current), Times.AtLeastOnce);
    }
}
