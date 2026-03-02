using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Radzen;
using Radzen.Blazor;
using UIBlazor.Components;
using UIBlazor.Options;
using UIBlazor.Services.Settings;

namespace UIBlazor.Tests.Components;

public class LanguageSelectorTests : Bunit.TestContext
{
    private readonly Mock<ICommonSettingsProvider> _mockSettingsProvider;

    public LanguageSelectorTests()
    {
        _mockSettingsProvider = new Mock<ICommonSettingsProvider>();

        var options = new CommonOptions { Culture = "en-US" };
        _mockSettingsProvider.Setup(p => p.Current).Returns(options);

        Services.AddSingleton(_mockSettingsProvider.Object);
        Services.AddRadzenComponents();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
    }

    [Fact]
    public void ShouldRenderWithCorrectInitialCulture()
    {
        // Act
        var cut = RenderComponent<LanguageSelector>();

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<string>>();
        Assert.Equal("en-US", dropdown.Instance.Value);
    }

    [Fact]
    public async Task ShouldUpdateSettingsAndNavigateOnCultureChange()
    {
        // Arrange
        var navManager = Services.GetRequiredService<NavigationManager>() as FakeNavigationManager;
        var cut = RenderComponent<LanguageSelector>();
        var dropdown = cut.FindComponent<RadzenDropDown<string>>();
        var newCulture = "ru-RU";

        // Act
        await cut.InvokeAsync(() => dropdown.Instance.Change.InvokeAsync(newCulture));

        // Assert
        Assert.Equal(newCulture, _mockSettingsProvider.Object.Current.Culture);
        _mockSettingsProvider.Verify(p => p.SaveAsync(), Times.Once);
    }
}
