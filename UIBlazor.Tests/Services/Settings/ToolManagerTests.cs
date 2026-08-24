namespace UIBlazor.Tests.Services.Settings;

public partial class ToolManagerTests
{
    private readonly ToolManager _toolManager;
    private readonly BuiltInAgent _builtInAgent;
    private readonly Mock<ILocalStorageService> _localStorageMock;
    private readonly Mock<IMcpSettingsProvider> _mcpSettingsMock;
    private readonly Mock<ICommonSettingsProvider> _commonSettingsMock;
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly McpOptions _mcpOptions;
    private readonly ILogger<ToolManager> _logger;
    private readonly NativeToolDefinition _nativeTool;

    public ToolManagerTests()
    {
        _localStorageMock = new Mock<ILocalStorageService>();
        _mcpSettingsMock = new Mock<IMcpSettingsProvider>();
        _commonSettingsMock = new Mock<ICommonSettingsProvider>();
        _vsBridgeMock = new Mock<IVsBridge>();
        _logger = new LoggerMock<ToolManager>();

        _mcpOptions = new McpOptions { Enabled = true };
        _mcpSettingsMock.Setup(m => m.Current).Returns(_mcpOptions);
        _nativeTool = new NativeToolDefinition()
        {
            Function = new NativeToolFunction
            {
                Description = "Test tool",
                Name = "test_tool",
                Parameters = new NativeParameters()
                {
                    Type = NativeToolType.String,
                    Properties = []
                }
            }
        };

        // Setup default tool
        var tool = new Tool
        {
            Name = "test_tool",
            Category = ToolCategory.ReadFiles,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "test result" })
        };
        _builtInAgent = new BuiltInAgent(_vsBridgeMock.Object, Mock.Of<ISkillService>(), Mock.Of<IInternalExecutor>()) { Tools = [tool] };

        _toolManager = new ToolManager(_builtInAgent, _logger, _localStorageMock.Object, _commonSettingsMock.Object, _mcpSettingsMock.Object, _vsBridgeMock.Object);
    }

    [Fact]
    public void RegisterAllTools_RegistersToolsFromAgent()
    {
        // Arrange
        var tool1 = new Tool { Name = "tool1", NativeTool = _nativeTool, ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "result1" }) };
        var tool2 = new Tool { Name = "tool2", NativeTool = _nativeTool, ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "result2" }) };
        _builtInAgent.Tools = [tool1, tool2];

        // Act
        _toolManager.RegisterAllTools();

        // Assert
        Assert.Equal(2, _toolManager.GetAllTools().Count());
        Assert.Contains(_toolManager.GetAllTools(), t => t.Name == "tool1");
        Assert.Contains(_toolManager.GetAllTools(), t => t.Name == "tool2");
    }

    [Fact]
    public async Task LoadToolSettingsAsync_HandlesExceptionGracefully()
    {
        // Arrange
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ToolSettings>(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Storage error"));

        // Act & Assert - should not throw
        await _toolManager.InitializeAsync();
    }

    [Fact]
    public void GetEnabledTools_ReturnsOnlyEnabledTools()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.GetTool("test_tool")!.Enabled = false;

        // Act
        var enabledTools = _toolManager.GetEnabledTools(AppMode.Agent);

        // Assert
        Assert.Empty(enabledTools);
    }

    [Fact]
    public void GetAllTools_ReturnsAllRegisteredTools()
    {
        // Arrange
        _toolManager.RegisterAllTools();

        // Act
        var allTools = _toolManager.GetAllTools().ToList();

        // Assert
        Assert.Single(allTools);
        Assert.Equal("test_tool", allTools.First().Name);
    }

    [Fact]
    public void GetTool_ReturnsToolByName()
    {
        // Arrange
        _toolManager.RegisterAllTools();

        // Act
        var tool = _toolManager.GetTool("test_tool");
        var nonexistentTool = _toolManager.GetTool("nonexistent");

        // Assert
        Assert.NotNull(tool);
        Assert.Equal("test_tool", tool.Name);
        Assert.Null(nonexistentTool);
    }
}
