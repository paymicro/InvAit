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
}
