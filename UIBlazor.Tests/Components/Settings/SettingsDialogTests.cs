namespace UIBlazor.Tests.Components.Settings;

/// <summary>
/// Tests for <see cref="SettingsDialog"/>
/// </summary>
public class SettingsDialogTests : BunitContext
{
    private readonly Mock<IChatService> _mockChatService;
    private readonly Mock<IToolManager> _mockToolManager;
    private readonly Mock<IProfileManager> _mockProfileManager;
    private readonly Mock<ICommonSettingsProvider> _mockCommonSettings;
    private readonly ConnectionProfile _profile;
    private readonly CommonOptions _commonOptions;

    public SettingsDialogTests()
    {
        _mockChatService = new Mock<IChatService>();
        _mockToolManager = new Mock<IToolManager>();
        _mockProfileManager = new Mock<IProfileManager>();
        _mockCommonSettings = new Mock<ICommonSettingsProvider>();

        _profile = new ConnectionProfile
        {
            Id = "test-profile-id",
            Name = "Test Profile"
        };

        var profileOptions = new ProfileOptions
        {
            ActiveProfileId = "test-profile-id",
            Profiles = [_profile]
        };

        _commonOptions = new CommonOptions
        {
            MaxRetries = 3,
            ToolTimeoutMs = 120000
        };

        _mockProfileManager.Setup(x => x.Current).Returns(profileOptions);
        _mockProfileManager.Setup(x => x.ActiveProfile).Returns(_profile);
        _mockCommonSettings.Setup(x => x.Current).Returns(_commonOptions);
        _mockToolManager.Setup(x => x.Current).Returns(new ToolSettings());
        _mockToolManager.Setup(x => x.GetBuiltInTools()).Returns([]);

        Services.AddSingleton(_mockChatService.Object);
        Services.AddSingleton(_mockToolManager.Object);
        Services.AddSingleton(_mockProfileManager.Object);
        Services.AddSingleton(_mockCommonSettings.Object);
        Services.AddSingleton(new Mock<ISkillService>().Object);

        Services.AddRadzenComponents();

        // Radzen Slider и др. вызывают JS-interop при рендеринге — глушим через Moq
        var mockJsRuntime = new Mock<IJSRuntime>();
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        Services.AddSingleton(mockJsRuntime.Object);

        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);

        ComponentFactories.AddStub<ModelSelector>(builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddContent(1, "ModelSelector Stub");
            builder.CloseElement();
        });

        ComponentFactories.AddStub<MCPSettingsTab>(builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "mcp-settings-stub");
            builder.AddContent(2, "MCP Stub");
            builder.CloseElement();
        });

        ComponentFactories.AddStub<ExtraHeaderPicker>(builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddContent(1, "ExtraHeaderPicker Stub");
            builder.CloseElement();
        });
    }

    [Fact]
    public void ShouldRenderFourTabs_WithIconHeaders()
    {
        // Arrange & Act
        var cut = Render<SettingsDialog>();

        // Assert
        var tabButtons = cut.FindAll(".rz-tabview-nav li button[role='tab']");
        Assert.Equal(4, tabButtons.Count);

        var expectedIcons = new[]
        {
            "fa-sliders",
            "fa-screwdriver-wrench",
            "fa-plug",
            "fa-file-lines"
        };

        for (var i = 0; i < expectedIcons.Length; i++)
        {
            var icon = tabButtons[i].QuerySelector("i");
            Assert.NotNull(icon);
            Assert.Contains(expectedIcons[i], icon!.ClassName);
        }
    }

    [Fact]
    public void ShouldNotRenderTextInTabHeaders()
    {
        // Arrange & Act
        var cut = Render<SettingsDialog>();

        // Assert - headers contain only icons, no localized titles
        foreach (var tabButton in cut.FindAll(".rz-tabview-nav li button[role='tab']"))
        {
            Assert.DoesNotContain(SharedResource.SettingsGeneral, tabButton.TextContent);
            Assert.DoesNotContain(SharedResource.SettingsTools, tabButton.TextContent);
            Assert.DoesNotContain("MCP", tabButton.TextContent);
        }
    }

    [Fact]
    public void ShouldHaveTooltips_OnTabHeaders()
    {
        // Arrange & Act
        var cut = Render<SettingsDialog>();

        // Assert - prompt tab moved to the last position (after MCP)
        var tabButtons = cut.FindAll(".rz-tabview-nav li button[role='tab']");
        Assert.Equal(SharedResource.SettingsGeneral, tabButtons[0].GetAttribute("title"));
        Assert.Equal(SharedResource.SettingsTools, tabButtons[1].GetAttribute("title"));
        Assert.Equal("MCP", tabButtons[2].GetAttribute("title"));
        Assert.Equal(SharedResource.SystemPromptSettings, tabButtons[3].GetAttribute("title"));
    }

    [Fact]
    public void MergedTab_ShouldContainPromptAndMiscSections()
    {
        // Arrange & Act
        var cut = Render<SettingsDialog>();

        // Select the merged prompt+misc tab (last position)
        var tabButtons = cut.FindAll(".rz-tabview-nav li button[role='tab']");
        tabButtons[tabButtons.Count - 1].Click();

        // Assert - prompt section
        Assert.NotNull(cut.FindComponent<SystemPromptSettings>());
        // misc section markers
        Assert.Contains(SharedResource.MaxRetries, cut.Find(".rz-tabview-panels").TextContent);
        Assert.Contains(SharedResource.ShowMessageTimings, cut.Find(".rz-tabview-panels").TextContent);
    }
}
