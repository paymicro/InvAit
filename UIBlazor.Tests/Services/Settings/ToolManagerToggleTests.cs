namespace UIBlazor.Tests.Services.Settings;

public partial class ToolManagerTests
{
    [Fact]
    public void ToggleTool_DisablesTool()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        Assert.True(_toolManager.GetTool("test_tool")!.Enabled);

        // Act
        _toolManager.ToggleTool("test_tool", false);

        // Assert
        Assert.False(_toolManager.GetTool("test_tool")!.Enabled);
    }

    [Fact]
    public void ToggleTool_EnablesTool()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.GetTool("test_tool")!.Enabled = false;

        // Act
        _toolManager.ToggleTool("test_tool", true);

        // Assert
        Assert.True(_toolManager.GetTool("test_tool")!.Enabled);
    }

    [Fact]
    public void ToggleTool_DoesNothingForNonExistentTool()
    {
        // Arrange - no tool with this name registered
        _toolManager.RegisterAllTools();

        // Act - should not throw
        _toolManager.ToggleTool("non_existent_tool", false);
    }
}
