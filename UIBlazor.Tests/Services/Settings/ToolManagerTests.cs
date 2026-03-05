using Microsoft.Extensions.Logging;
using Moq;
using Shared.Contracts;
using UIBlazor.Agents;
using UIBlazor.Models;
using UIBlazor.Options;
using UIBlazor.Services;
using UIBlazor.Services.Settings;
using UIBlazor.Tests.Utils;
using UIBlazor.VS;

namespace UIBlazor.Tests.Services.Settings;

public class ToolManagerTests
{
    private readonly IToolManager _toolManager;
    private readonly BuiltInAgent _builtInAgent;
    private readonly Mock<ILocalStorageService> _localStorageMock;
    private readonly ILogger<ToolManager> _logger;

    public ToolManagerTests()
    {
        _localStorageMock = new Mock<ILocalStorageService>();
        var mcpSettingsMock = new Mock<IMcpSettingsProvider>();
        mcpSettingsMock.Setup(m => m.Current).Returns(new McpOptions());
        var vsBridgeMock = new Mock<IVsBridge>();
        _logger = new LoggerMock<ToolManager>();

        // Setup default tool
        var tool = new Tool
        {
            Name = "test_tool",
            Description = "Test tool",
            ExecuteAsync = _ => Task.FromResult(new VsToolResult { Success = true, Result = "test result" })
        };
        _builtInAgent = new BuiltInAgent(vsBridgeMock.Object, Mock.Of<ISkillService>()) { Tools = [tool] };

        _toolManager = new ToolManager(_builtInAgent, _logger, _localStorageMock.Object, mcpSettingsMock.Object, vsBridgeMock.Object);
    }

    [Fact]
    public void RegisterAllTools_RegistersToolsFromAgent()
    {
        // Arrange
        var tool1 = new Tool { Name = "tool1", ExecuteAsync = _ => Task.FromResult(new VsToolResult { Success = true, Result = "result1" }) };
        var tool2 = new Tool { Name = "tool2", ExecuteAsync = _ => Task.FromResult(new VsToolResult { Success = true, Result = "result2" }) };
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
        var enabledTools = _toolManager.GetEnabledTools();

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

    [Fact]
    public void GetToolUseSystemInstructions_ReturnsFormattedInstructions()
    {
        // Arrange
        _toolManager.RegisterAllTools();

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Agent, false);

        // Assert
        Assert.Contains("test_tool", instructions);
        Assert.Contains("Test tool", instructions);
        Assert.Contains("tool-calling agent", instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_ReturnsNoInstructionsWhenNoEnabledTools()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.GetTool("test_tool")!.Enabled = false;

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Agent, false);

        // Assert
        Assert.DoesNotContain("Tool use instructions", instructions);
    }
}
