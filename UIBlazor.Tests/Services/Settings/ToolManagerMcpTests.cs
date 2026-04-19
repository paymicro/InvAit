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
    private readonly Mock<ILocalStorageService> _storageMock;
    private readonly ToolManager _toolManager;
    private readonly McpOptions _mcpOptions;
    private readonly ILogger<ToolManager> _logger;
    private readonly BuiltInAgent _builtInAgent;

    public ToolManagerMcpTests()
    {
        _mcpSettingsMock = new Mock<IMcpSettingsProvider>();
        _vsBridgeMock = new Mock<IVsBridge>();
        _storageMock = new Mock<ILocalStorageService>();
        _logger = new LoggerMock<ToolManager>();

        _builtInAgent = new BuiltInAgent(_vsBridgeMock.Object, Mock.Of<ISkillService>(), Mock.Of<IInternalExecutor>());
        foreach (var item in _builtInAgent.Tools)
        {
            item.Enabled = false;
        }

        _mcpOptions = new McpOptions { Enabled = true };
        _mcpSettingsMock.Setup(m => m.Current).Returns(_mcpOptions);

        _toolManager = new ToolManager(_builtInAgent, _logger, _storageMock.Object, _mcpSettingsMock.Object, _vsBridgeMock.Object);
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

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.McpCallTool, It.IsAny<IReadOnlyDictionary<string, object>?>(), CancellationToken.None))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "ok" });

        // Act
        await mcpTool.ExecuteAsync(args, CancellationToken.None);

        // Assert
        _vsBridgeMock.Verify(v => v.ExecuteToolAsync(BasicEnum.McpCallTool, It.Is<Dictionary<string, object>>(dict => 
            dict["serverId"].ToString() == "my-server" &&
            dict["toolName"].ToString() == "my-tool" &&
            ((Dictionary<string, object>)dict["arguments"])["arg1"].ToString() == "val1" &&
            !((Dictionary<string, object>)dict["arguments"]).ContainsKey("other")
        ), CancellationToken.None), Times.Once);
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

    #region BuildMcpTools Tests

    [Fact]
    public void BuildMcpTools_ReturnsEmptyWhenMcpDisabled()
    {
        // Arrange
        _mcpOptions.Enabled = false;
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "test-tool" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tools = _toolManager.BuildMcpTools().ToList();

        // Assert
        Assert.Empty(tools);
    }

    [Fact]
    public void BuildMcpTools_ReturnsEmptyWhenNoServers()
    {
        // Arrange
        _mcpOptions.Servers.Clear();

        // Act
        var tools = _toolManager.BuildMcpTools().ToList();

        // Assert
        Assert.Empty(tools);
    }

    [Fact]
    public void BuildMcpTools_FiltersDisabledServers()
    {
        // Arrange
        var enabledServer = new McpServerConfig
        {
            Name = "enabled-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "tool1" }]
        };
        var disabledServer = new McpServerConfig
        {
            Name = "disabled-server",
            Enabled = false,
            Tools = [new McpToolConfig { Name = "tool2" }]
        };
        _mcpOptions.Servers.Add(enabledServer);
        _mcpOptions.Servers.Add(disabledServer);

        // Act
        var tools = _toolManager.BuildMcpTools().ToList();

        // Assert
        Assert.Single(tools);
        Assert.Equal("mcp__enabled-server__tool1", tools[0].Name);
    }

    [Fact]
    public void BuildMcpTools_RespectsServerEnabledStates()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true, // Default enabled
            Tools = [new McpToolConfig { Name = "tool1" }]
        };
        _mcpOptions.Servers.Add(server);
        _mcpOptions.ServerEnabledStates["test-server"] = false; // But explicitly disabled in states

        // Act
        var tools = _toolManager.BuildMcpTools().ToList();

        // Assert
        Assert.Empty(tools);
    }

    [Fact]
    public void BuildMcpTools_SetsCorrectToolProperties()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "my-server",
            Command = "node",
            Args = ["server.js"],
            Env = new Dictionary<string, string> { { "API_KEY", "secret" } },
            Tools = [new McpToolConfig { Name = "my-tool", Description = "My tool description" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tools = _toolManager.BuildMcpTools().ToList();

        // Assert
        var tool = tools[0];
        Assert.Equal("mcp__my-server__my-tool", tool.Name);
        Assert.Equal("my-tool", tool.DisplayName);
        Assert.Equal("My tool description", tool.Description);
        Assert.Equal(ToolCategory.Mcp, tool.Category);
        Assert.True(tool.Enabled);
    }

    [Fact]
    public void BuildMcpTools_GeneratesCorrectToolName()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "server-name",
            Tools = [new McpToolConfig { Name = "tool-name" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tools = _toolManager.BuildMcpTools().ToList();

        // Assert
        Assert.Equal("mcp__server-name__tool-name", tools[0].Name);
    }

    #endregion

    #region GetMcpTools Caching Tests

    [Fact]
    public void GetMcpTools_ReturnsToolsFromServer()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "tool1" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tools = _toolManager.GetMcpTools().ToList();

        // Assert
        Assert.Single(tools);
        Assert.Equal("mcp__test-server__tool1", tools[0].Name);
    }

    [Fact]
    public void GetMcpTools_ReturnsEmptyListWhenMcpDisabled()
    {
        // Arrange
        _mcpOptions.Enabled = false;

        // Act
        var tools = _toolManager.GetMcpTools().ToList();

        // Assert
        Assert.Empty(tools);
    }

    [Fact]
    public void GetMcpTools_ReflectsServerChanges()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "tool1" }]
        };
        _mcpOptions.Servers.Add(server);

        // First call
        var firstCall = _toolManager.GetMcpTools().ToList();
        Assert.Single(firstCall);
        Assert.Equal("mcp__test-server__tool1", firstCall[0].Name);

        // Add new server
        var newServer = new McpServerConfig
        {
            Name = "new-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "new-tool" }]
        };
        _mcpOptions.Servers.Add(newServer);

        // Act - create new ToolManager to reflect changes (simulates restart)
        var newToolManager = new ToolManager(_builtInAgent, _logger, _storageMock.Object, _mcpSettingsMock.Object, _vsBridgeMock.Object);
        var secondCall = newToolManager.GetMcpTools().ToList();

        // Assert - should have tools from both servers
        Assert.Equal(2, secondCall.Count);
        Assert.Contains(secondCall, t => t.Name == "mcp__test-server__tool1");
        Assert.Contains(secondCall, t => t.Name == "mcp__new-server__new-tool");
    }

    #endregion

    #region MCP Tool Execution Tests

    [Fact]
    public async Task McpTool_ExecuteAsync_PassesServerCommandAndArgs()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "my-server",
            Command = "npx",
            Args = ["-y", "@modelcontextprotocol/server-everything"],
            Env = new Dictionary<string, string> { { "NODE_ENV", "test" } },
            Tools = [new McpToolConfig { Name = "my-tool" }]
        };
        _mcpOptions.Servers.Add(server);

        var mcpTool = _toolManager.GetAllTools().First(t => t.Name == "mcp__my-server__my-tool");
        var args = new Dictionary<string, object>();

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.McpCallTool, It.IsAny<IReadOnlyDictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "ok" });

        // Act
        await mcpTool.ExecuteAsync(args, CancellationToken.None);

        // Assert
        _vsBridgeMock.Verify(v => v.ExecuteToolAsync(BasicEnum.McpCallTool, It.Is<Dictionary<string, object>>(dict =>
            dict["command"].ToString() == "npx" &&
            dict["args"].ToString() == "-y @modelcontextprotocol/server-everything" &&
            dict["env"] != null
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpTool_ExecuteAsync_ReturnsResult()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Tools = [new McpToolConfig { Name = "test-tool" }]
        };
        _mcpOptions.Servers.Add(server);

        var mcpTool = _toolManager.GetAllTools().First(t => t.Name == "mcp__test-server__test-tool");
        var expectedResult = new VsToolResult { Success = true, Result = "tool result" };

        _vsBridgeMock.Setup(v => v.ExecuteToolAsync(BasicEnum.McpCallTool, It.IsAny<IReadOnlyDictionary<string, object>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await mcpTool.ExecuteAsync(new Dictionary<string, object>(), CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("tool result", result.Result);
    }

    #endregion

    #region MCP Tool with Complex Schema Tests

    [Fact]
    public void BuildMcpTools_HandlesComplexInputSchema()
    {
        // Arrange
        var schemaJson = """
        {
            "type": "object",
            "properties": {
                "path": { "type": "string", "description": "File path" },
                "options": {
                    "type": "object",
                    "properties": {
                        "recursive": { "type": "boolean" }
                    }
                },
                "count": { "type": "integer", "minimum": 0, "maximum": 100 }
            }
        }
        """;
        var server = new McpServerConfig
        {
            Name = "test-server",
            Tools = [new McpToolConfig
            {
                Name = "complex-tool",
                Description = "Tool with complex schema",
                InputSchema = JsonDocument.Parse(schemaJson).RootElement
            }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tools = _toolManager.BuildMcpTools().ToList();

        // Assert
        Assert.Single(tools);
        Assert.NotNull(tools[0].ExampleToSystemMessage);
        Assert.Contains("complex-tool", tools[0].ExampleToSystemMessage);
    }

    [Fact]
    public void BuildMcpTools_HandlesNullInputSchema()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Tools = [new McpToolConfig
            {
                Name = "no-schema-tool",
                Description = "Tool without schema",
                InputSchema = null
            }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tools = _toolManager.BuildMcpTools().ToList();

        // Assert
        Assert.Single(tools);
        Assert.NotNull(tools[0]);
    }

    [Fact]
    public void BuildMcpTools_HandlesEnumInSchema()
    {
        // Arrange
        var schemaJson = """
        {
            "type": "object",
            "properties": {
                "mode": {
                    "type": "string",
                    "enum": ["read", "write", "delete"],
                    "description": "Operation mode"
                }
            }
        }
        """;
        var server = new McpServerConfig
        {
            Name = "test-server",
            Tools = [new McpToolConfig
            {
                Name = "enum-tool",
                InputSchema = JsonDocument.Parse(schemaJson).RootElement
            }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tools = _toolManager.BuildMcpTools().ToList();

        // Assert
        Assert.Single(tools);
        Assert.Contains("enum", tools[0].ExampleToSystemMessage);
    }

    #endregion

    #region GetEnabledTools with MCP Tests

    [Fact]
    public void GetEnabledTools_IncludesEnabledMcpTools()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "enabled-tool" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var enabledTools = _toolManager.GetEnabledTools().ToList();

        // Assert
        Assert.Contains(enabledTools, t => t.Name == "mcp__test-server__enabled-tool");
    }

    [Fact]
    public void GetEnabledTools_ExcludesDisabledMcpTools()
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
        var enabledTools = _toolManager.GetEnabledTools().ToList();

        // Assert
        Assert.DoesNotContain(enabledTools, t => t.Name == toolName);
    }

    #endregion

    #region GetTool with MCP Tests

    [Fact]
    public void GetTool_ReturnsMcpToolByName()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Tools = [new McpToolConfig { Name = "my-tool" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tool = _toolManager.GetTool("mcp__test-server__my-tool");

        // Assert
        Assert.NotNull(tool);
        Assert.Equal("mcp__test-server__my-tool", tool.Name);
    }

    [Fact]
    public void GetTool_ReturnsNullForNonExistentMcpTool()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Tools = [new McpToolConfig { Name = "my-tool" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var tool = _toolManager.GetTool("mcp__test-server__nonexistent");

        // Assert
        Assert.Null(tool);
    }

    #endregion

    #region Multiple Servers Tests

    [Fact]
    public void GetAllTools_IncludesToolsFromMultipleServers()
    {
        // Arrange
        var server1 = new McpServerConfig
        {
            Name = "server1",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "tool1" }]
        };
        var server2 = new McpServerConfig
        {
            Name = "server2",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "tool2" }]
        };
        _mcpOptions.Servers.Add(server1);
        _mcpOptions.Servers.Add(server2);

        // Act
        var tools = _toolManager.GetAllTools().ToList();

        // Assert
        Assert.Contains(tools, t => t.Name == "mcp__server1__tool1");
        Assert.Contains(tools, t => t.Name == "mcp__server2__tool2");
    }

    [Fact]
    public void GetApprovalModeByToolName_ReturnsCorrectModeForEachServer()
    {
        // Arrange
        _mcpOptions.ServerApprovalModes["server1"] = ToolApprovalMode.Ask;
        _mcpOptions.ServerApprovalModes["server2"] = ToolApprovalMode.Deny;

        // Act
        var mode1 = _toolManager.GetApprovalModeByToolName("mcp__server1__any-tool");
        var mode2 = _toolManager.GetApprovalModeByToolName("mcp__server2__any-tool");

        // Assert
        Assert.Equal(ToolApprovalMode.Ask, mode1);
        Assert.Equal(ToolApprovalMode.Deny, mode2);
    }

    #endregion

    #region GetToolUseSystemInstructions with MCP Tests

    [Fact]
    public void GetToolUseSystemInstructions_IncludesMcpTools()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "my-tool", Description = "My MCP tool" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Agent, false);

        // Assert
        Assert.Contains("mcp__test-server__my-tool", instructions);
        Assert.Contains("My MCP tool", instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_IncludesMcpToolsInChatMode()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "my-tool", Description = "My MCP tool" }]
        };
        _mcpOptions.Servers.Add(server);

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Chat, false);

        // Assert
        Assert.Contains("mcp__test-server__my-tool", instructions);
    }

    #endregion
}
