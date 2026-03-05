using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Shared.Contracts;
using Shared.Contracts.Mcp;
using UIBlazor.Agents;
using UIBlazor.Models;
using UIBlazor.Options;
using UIBlazor.Services;
using UIBlazor.Services.Settings;
using UIBlazor.Tests.Utils;
using UIBlazor.VS;

namespace UIBlazor.Tests.Services.Settings;

public class ToolManagerMcpTests
{
    private readonly Mock<IMcpSettingsProvider> _mcpSettingsMock;
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly ToolManager _toolManager;
    private readonly McpOptions _mcpOptions;
    private readonly ILogger<ToolManager> _logger;

    public ToolManagerMcpTests()
    {
        _mcpSettingsMock = new Mock<IMcpSettingsProvider>();
        _vsBridgeMock = new Mock<IVsBridge>();
        var storageMock = new Mock<ILocalStorageService>();
        var builtInAgent = new BuiltInAgent(_vsBridgeMock.Object, Mock.Of<ISkillService>());
        _logger = new LoggerMock<ToolManager>();
        foreach (var item in builtInAgent.Tools)
        {
            item.Enabled = false;
        }

        _mcpOptions = new McpOptions { Enabled = true };
        _mcpSettingsMock.Setup(m => m.Current).Returns(_mcpOptions);

        _toolManager = new ToolManager(builtInAgent, _logger, storageMock.Object, _mcpSettingsMock.Object, _vsBridgeMock.Object);
    }

    [Fact]
    public void GetAllTools_IncludesMcpTools()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "test-tool", Description = "Test MCP Tool" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tools = _toolManager.GetAllTools().ToList();

        // Assert
        var mcpTool = tools.FirstOrDefault(t => t.Name == "mcp__test-server__test-tool");
        Assert.NotNull(mcpTool);
        Assert.Equal("test-tool", mcpTool.DisplayName);
        Assert.Equal("Test MCP Tool", mcpTool.Description);
        Assert.Equal(ToolCategory.Mcp, mcpTool.Category);
    }

    [Fact]
    public void GetAllTools_RespectsToolEnabledState()
    {
        // Arrange
        var toolName = "mcp__test-server__disabled-tool";
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "disabled-tool" }]
        };
        _mcpOptions.Servers.Add(server);
        _mcpOptions.ToolDisabledStates.Add(toolName);

        // Act
        var tools = _toolManager.GetAllTools().ToList();
        var enabledTools = _toolManager.GetEnabledTools().ToList();

        // Assert
        Assert.Contains(tools, t => t.Name == toolName);
        Assert.DoesNotContain(enabledTools, t => t.Name == toolName);
    }

    [Fact]
    public async Task McpTool_ExecuteAsync_CallsVsBridgeWithProperArgs()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "my-server",
            Tools = [new McpToolConfig 
            { 
                Name = "my-tool",
                InputSchema = JsonDocument.Parse("{\"properties\": {\"arg1\": {\"type\": \"string\"}}}").RootElement
            }]
        };
        _mcpOptions.Servers.Add(server);
        _mcpOptions.ServerEnabledStates["my-server"] = true;

        var mcpTool = _toolManager.GetAllTools().First(t => t.Name == "mcp__my-server__my-tool");
        var args = new Dictionary<string, object> { { "arg1", "val1" }, { "other", "ignored" } };

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.McpCallTool, It.IsAny<IReadOnlyDictionary<string, object>?>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "ok" });

        // Act
        await mcpTool.ExecuteAsync(args);

        // Assert
        _vsBridgeMock.Verify(v => v.ExecuteToolAsync(BasicEnum.McpCallTool, It.Is<Dictionary<string, object>>(dict => 
            dict["serverId"].ToString() == "my-server" &&
            dict["toolName"].ToString() == "my-tool" &&
            ((Dictionary<string, object>)dict["arguments"])["arg1"].ToString() == "val1" &&
            !((Dictionary<string, object>)dict["arguments"]).ContainsKey("other")
        )), Times.Once);
    }

    [Fact]
    public void GetApprovalModeByToolName_ReturnsCorrectMode()
    {
        // Arrange
        var serverName = "protected-server";
        var toolName = $"mcp__{serverName}__any-tool";
        _mcpOptions.ServerApprovalModes[serverName] = ToolApprovalMode.Ask;

        // Act
        var mode = _toolManager.GetApprovalModeByToolName(toolName);

        // Assert
        Assert.Equal(ToolApprovalMode.Ask, mode);
    }

    [Fact]
    public void GetApprovalModeByToolName_DefaultsToAutoApprove()
    {
        // Act
        var mode = _toolManager.GetApprovalModeByToolName("mcp__unknown__tool");

        // Assert
        Assert.Equal(ToolApprovalMode.Allow, mode);
    }
}
