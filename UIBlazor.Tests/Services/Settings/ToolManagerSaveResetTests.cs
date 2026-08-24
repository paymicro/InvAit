namespace UIBlazor.Tests.Services.Settings;

public partial class ToolManagerTests
{
    [Fact]
    public async Task SaveAsync_UpdatesCategoryStatesForNewCategories()
    {
        // Arrange
        var tool1 = new Tool { Name = "tool1", NativeTool = _nativeTool, Category = ToolCategory.Execution, ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true }) };
        var tool2 = new Tool { Name = "tool2", NativeTool = _nativeTool, Category = ToolCategory.DeleteFiles, ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true }) };
        _builtInAgent.Tools = [tool1, tool2];
        _toolManager.RegisterAllTools();

        // Act
        await _toolManager.SaveAsync();

        // Assert
        Assert.True(_toolManager.Current.CategoryStates.ContainsKey(ToolCategory.Execution));
        Assert.True(_toolManager.Current.CategoryStates.ContainsKey(ToolCategory.DeleteFiles));
    }

    [Fact]
    public async Task SaveAsync_UpdatesDisabledToolsList()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.GetTool("test_tool")!.Enabled = false;

        // Act
        await _toolManager.SaveAsync();

        // Assert
        Assert.Contains("test_tool", _toolManager.Current.DisabledTools);
    }

    [Fact]
    public async Task SaveAsync_DoesNotIncludeEnabledToolsInDisabledList()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.GetTool("test_tool")!.Enabled = true;

        // Act
        await _toolManager.SaveAsync();

        // Assert
        Assert.DoesNotContain("test_tool", _toolManager.Current.DisabledTools);
    }

    [Fact]
    public async Task ResetAsync_EnablesAllTools()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.GetTool("test_tool")!.Enabled = false;

        // Act
        await _toolManager.ResetAsync();

        // Assert
        Assert.True(_toolManager.GetTool("test_tool")!.Enabled);
    }

    [Fact]
    public async Task ResetAsync_ResetsCategoryStates()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.Current.CategoryStates[ToolCategory.ReadFiles] = new ToolCategorySettings
        {
            IsEnabled = false,
            ApprovalMode = ToolApprovalMode.Deny
        };

        // Act
        await _toolManager.ResetAsync();

        // Assert
        var state = _toolManager.Current.CategoryStates[ToolCategory.ReadFiles];
        Assert.True(state.IsEnabled);
        Assert.Equal(ToolApprovalMode.Allow, state.ApprovalMode);
    }

    [Fact]
    public async Task ResetAsync_ClearsDisabledTools()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.Current.DisabledTools.Add("some_tool");

        // Act
        await _toolManager.ResetAsync();

        // Assert
        Assert.Empty(_toolManager.Current.DisabledTools);
    }

    [Fact]
    public async Task InitializeAsync_LoadsDisabledToolsFromSettings()
    {
        // Arrange
        var savedSettings = new ToolSettings();
        savedSettings.DisabledTools.Add("test_tool");
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ToolSettings>("ToolSettings"))
            .ReturnsAsync(savedSettings);

        // Act
        await _toolManager.InitializeAsync();
        _toolManager.RegisterAllTools();
        await _toolManager.InitializeAsync(); // Called again by RegisterAllTools internally

        // Assert
        Assert.False(_toolManager.GetTool("test_tool")!.Enabled);
    }
}
