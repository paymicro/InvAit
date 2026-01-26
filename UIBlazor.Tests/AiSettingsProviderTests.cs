using Microsoft.JSInterop;
using Moq;
using Shared.Contracts;
using UIBlazor.Options;
using UIBlazor.Services.Settings;

namespace UIBlazor.Tests;

public class AiSettingsProviderTests : IDisposable
{
    private readonly Mock<ILocalStorageService> _localStorageMock;
    private readonly Mock<IJSRuntime> _jsRuntimeMock;
    private readonly AiSettingsProvider _provider;

    public AiSettingsProviderTests()
    {
        _localStorageMock = new Mock<ILocalStorageService>();
        _jsRuntimeMock = new Mock<IJSRuntime>();
        _provider = new AiSettingsProvider(_localStorageMock.Object, _jsRuntimeMock.Object);
    }

    public void Dispose()
    {
        _provider.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_LoadsSettingsFromStorage()
    {
        // Arrange
        var savedOptions = new AiOptions
        {
            Endpoint = "saved-endpoint",
            ApiKey = "saved-key",
            Temperature = 0.9
        };
        _localStorageMock.Setup(ls => ls.GetItemAsync<AiOptions>("AiSettings"))
            .ReturnsAsync(savedOptions);

        // Act
        await _provider.InitializeAsync();

        // Assert
        Assert.Equal("saved-endpoint", _provider.Current.Endpoint);
        Assert.Equal("saved-key", _provider.Current.ApiKey);
        Assert.Equal(0.9, _provider.Current.Temperature);
    }

    [Fact]
    public async Task ResetAsync_ResetsToDefaultsAndSaves()
    {
        // Arrange
        _provider.Current.Temperature = 0.1;
        _provider.Current.SystemPrompt = "changed";

        // Act
        await _provider.ResetAsync();

        // Assert
        Assert.Equal(0.7, _provider.Current.Temperature);
        Assert.Equal("You are a helpful AI code assistant.", _provider.Current.SystemPrompt);
        _localStorageMock.Verify(ls => ls.SetItemAsync("AiSettings", _provider.Current), Times.Once);
    }

    [Fact]
    public void PropertyChanged_SkipSSL_InvokesJs()
    {
        // Arrange
        // We need to verify that setting SkipSSL triggers JS call
        
        // Act
        _provider.Current.SkipSSL = true;

        // Assert
        _jsRuntimeMock.Verify(js => js.InvokeAsync<string>("postVsMessage", It.Is<object[]>(args => 
            CheckSkipSslRequest(args)
        )), Times.Once);
    }

    private static bool CheckSkipSslRequest(object[] args)
    {
        if (args.Length != 1) return false;
        if (args[0] is not VsRequest req) return false;
        return req.Action == nameof(AiOptions.SkipSSL) && req.Payload == "True";
    }

    [Fact]
    public void PropertyChanged_OtherProperty_DoesNotInvokeJs()
    {
        // Act
        _provider.Current.Temperature = 0.5;

        // Assert
        _jsRuntimeMock.Verify(js => js.InvokeAsync<string>("postVsMessage", It.IsAny<object[]>()), Times.Never);
    }
}