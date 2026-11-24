namespace UIBlazor.Tests.Services.Settings;

public partial class ToolManagerTests
{
    [Fact]
    public void McpSettingsProviderOnSaved_ClearsMcpToolsCache()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "test-tool", Description = "Test", InputSchema = JsonSerializer.SerializeToElement("{}") }]
        };
        _mcpOptions.Servers.Add(server);
        _toolManager.RegisterAllTools();

        // First call to populate cache
        var firstCallTools = _toolManager.GetMcpTools().ToList();
        Assert.Single(firstCallTools);

        // Add new tool to server
        server.Tools.Add(new McpToolConfig { Name = "new-tool", Description = "New", InputSchema = JsonSerializer.SerializeToElement("{}") });

        // Act - trigger OnSaved event
        _mcpSettingsMock.Raise(m => m.OnSaved += null);

        // Assert - cache should be cleared, new tool should appear
        var secondCallTools = _toolManager.GetMcpTools().ToList();
        Assert.Equal(2, secondCallTools.Count);
    }

    [Fact]
    public void GetMcpTools_ToolsArriveWithoutOnSaved_AppearWithoutAnyEvent()
    {
        // The reported defect: at startup the servers are known but their TOOLS are not - the
        // schemas arrive from the MCP host afterwards. A cache filled in that window held an empty
        // list, and recovering it depended on the OnSaved event. No event ever came, so the chat
        // offered no MCP tools while the settings tab (reading Current.Servers directly) showed
        // them enabled. Toggling a checkbox raised OnSaved and "fixed" it.
        var server = new McpServerConfig { Name = "srv", Enabled = true, Tools = [] };
        _mcpOptions.Servers.Add(server);
        _toolManager.RegisterAllTools();

        // Startup: the server exists, the tools have not been fetched yet - cache warms up empty.
        Assert.Empty(_toolManager.GetMcpTools());

        // The host answers; the config is filled in place, exactly as UpdateServerToolsAsync does
        // (server.Tools = newTools). No OnSaved is raised here on purpose.
        server.Tools = [new McpToolConfig
        {
            Name = "sequentialthinking",
            Description = "Thinking",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
        }];

        // Act - no refresh call, no event.
        var tools = _toolManager.GetMcpTools().ToList();

        // Assert - the cache noticed the change on its own.
        var tool = Assert.Single(tools);
        Assert.Equal("mcp__srv__sequentialthinking", tool.Name);
        Assert.Equal("sequentialthinking", tool.DisplayName);
        Assert.Equal(ToolCategory.Mcp, tool.Category);
    }

    [Fact]
    public void GetMcpTools_ServersReloaded_ClosuresPointAtTheNewConfig()
    {
        // A reload replaces the server LIST (a new instance from storage). Names and tool names can
        // be identical, so a cache keyed on names alone would keep closures over the OLD server and
        // call a stale command/url.
        var oldServer = new McpServerConfig
        {
            Name = "srv",
            Enabled = true,
            Command = "old-cmd",
            Tools = [new McpToolConfig { Name = "t", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }]
        };
        _mcpOptions.Servers.Add(oldServer);
        _toolManager.RegisterAllTools();
        var oldTool = Assert.Single(_toolManager.GetMcpTools());

        // Act - the settings file is re-read into a NEW list with the same names.
        var newServer = new McpServerConfig
        {
            Name = "srv",
            Enabled = true,
            Command = "new-cmd",
            Tools = [new McpToolConfig { Name = "t", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }]
        };
        _mcpOptions.Servers = [newServer];

        // Assert - the old cached Tool captured a closure over the OLD config; it must not be
        // reused, otherwise the call would be sent with the retired command/url.
        var newTool = Assert.Single(_toolManager.GetMcpTools());
        Assert.NotSame(oldTool, newTool);
        Assert.Same("srv", newTool.Server);
        Assert.Equal("mcp__srv__t", newTool.Name);
    }

    [Fact]
    public void GetEnabledTools_McpDisableKey_FiltersByServerAndDisplayName()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
        _mcpOptions.Servers.Add(new McpServerConfig
        {
            Name = "srv",
            Enabled = true,
            Tools =
            [
                new McpToolConfig { Name = "tool1", InputSchema = schema },
                new McpToolConfig { Name = "tool2", InputSchema = schema }
            ]
        });
        _mcpOptions.ToolDisabledStates.Add("srv:tool1");
        _toolManager.RegisterAllTools();

        var mcpDisplayNames = _toolManager.GetEnabledTools(AppMode.Agent)
            .Where(t => t.Category == ToolCategory.Mcp)
            .Select(t => t.DisplayName)
            .ToList();

        Assert.Equal(["tool2"], mcpDisplayNames);
    }

    [Fact]
    public async Task BuildMcpTools_ExecuteAsync_SendsToolTimeoutFromSettings()
    {
        // The MCP call is the second channel that carries `timeoutMs`. It was hard-coded/hoped to
        // be present, so the host fell back to its own default and the user's limit applied to bash
        // only. One setting must drive both channels.
        var options = new CommonOptions { ToolTimeoutMs = 45_000 };
        _commonSettingsMock.Setup(m => m.Current).Returns(options);
        _mcpOptions.Servers.Add(new McpServerConfig
        {
            Name = "srv",
            Enabled = true,
            Tools =
            [
                new McpToolConfig
                {
                    Name = "t",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
                }
            ]
        });
        string? sentArgs = null;
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, CancellationToken>((_, args, _) => sentArgs = args)
            .ReturnsAsync(new VsToolResult());
        var mcpTool = Assert.Single(_toolManager.GetMcpTools());

        // Act
        await mcpTool.ExecuteAsync("{}", CancellationToken.None);

        // Assert - the setting, not a constant, and the tool arguments survive next to it.
        Assert.NotNull(sentArgs);
        var doc = JsonDocument.Parse(sentArgs!);
        Assert.Equal(45_000, doc.RootElement.GetProperty("timeoutMs").GetInt32());
        Assert.Equal("srv", doc.RootElement.GetProperty("serverId").GetString());
        Assert.Equal("t", doc.RootElement.GetProperty("toolName").GetString());
    }

    [Fact]
    public void GetApprovalModeByToolName_McpDunderName_UsesServerApprovalMode()
    {
        _mcpOptions.ServerApprovalModes["my"] = ToolApprovalMode.Ask;

        var mode = _toolManager.GetApprovalModeByToolName("mcp__my__sub__tool");

        Assert.Equal(ToolApprovalMode.Ask, mode);
    }
}
