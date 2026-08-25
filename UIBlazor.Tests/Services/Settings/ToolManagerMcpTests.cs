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
    public void GetApprovalModeByToolName_McpDunderName_UsesServerApprovalMode()
    {
        _mcpOptions.ServerApprovalModes["my"] = ToolApprovalMode.Ask;

        var mode = _toolManager.GetApprovalModeByToolName("mcp__my__sub__tool");

        Assert.Equal(ToolApprovalMode.Ask, mode);
    }
}
