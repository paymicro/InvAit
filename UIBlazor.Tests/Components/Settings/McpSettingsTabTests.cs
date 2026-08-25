namespace UIBlazor.Tests.Components.Settings;

/// <summary>
/// Basic rendering tests for <see cref="MCPSettingsTab"/> (heavy interactions are covered via services tests).
/// </summary>
public class McpSettingsTabTests : BunitContext
{
    private readonly Mock<IMcpSettingsProvider> _mockMcpSettings;
    private readonly Mock<IToolManager> _mockToolManager;
    private readonly Mock<IProfileManager> _mockProfileManager;
    private readonly McpOptions _options;

    public McpSettingsTabTests()
    {
        _mockMcpSettings = new Mock<IMcpSettingsProvider>();
        _mockToolManager = new Mock<IToolManager>();
        _mockProfileManager = new Mock<IProfileManager>();
        _options = new McpOptions { Enabled = false };

        _mockMcpSettings.Setup(x => x.Current).Returns(_options);
        _mockProfileManager.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile());
        _mockToolManager.Setup(x => x.GetBuiltInTools()).Returns([]);

        Services.AddSingleton(_mockMcpSettings.Object);
        Services.AddSingleton(_mockToolManager.Object);
        Services.AddSingleton(_mockProfileManager.Object);
        Services.AddRadzenComponents();

        var mockJsRuntime = new Mock<IJSRuntime>();
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        Services.AddSingleton(mockJsRuntime.Object);

        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
    }

    [Fact]
    public void DisabledState_RendersHeaderControls_WithoutServerList()
    {
        // Arrange & Act - MCP globally disabled
        var cut = Render<MCPSettingsTab>();

        // Assert
        Assert.NotNull(cut.FindComponent<RadzenButton>());
        // No server entries rendered
        Assert.DoesNotContain("server-commands", cut.Markup);
    }

    [Fact]
    public void EnabledStateWithoutServers_ShowsNoMcpMessage()
    {
        // Arrange
        _options.Enabled = true;

        // Act
        var cut = Render<MCPSettingsTab>();

        // Assert
        Assert.Contains(SharedResource.NoMCP, cut.Markup);
    }

    [Fact]
    public void EnabledStateWithDisabledServer_RendersServerEntry()
    {
        // Arrange
        _options.Enabled = true;
        _options.Servers.Add(new McpServerConfig
        {
            Name = "my-server",
            Transport = "stdio",
            Enabled = false,
            Command = "npx",
            Args = ["-y", "some-server"]
        });

        // Act
        var cut = Render<MCPSettingsTab>();

        // Assert - server name shown as checkbox text, no command line while disabled
        Assert.Contains("my-server", cut.Markup);
        Assert.DoesNotContain("server-commands", cut.Markup);
    }
}
