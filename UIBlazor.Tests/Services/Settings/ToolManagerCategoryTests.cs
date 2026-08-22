namespace UIBlazor.Tests.Services.Settings;

public partial class ToolManagerTests
{
    [Fact]
    public void UpdateCategorySettings_UpdatesExistingCategory()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.Current.CategoryStates[ToolCategory.ReadFiles] = new ToolCategorySettings
        {
            IsEnabled = true,
            ApprovalMode = ToolApprovalMode.Allow
        };

        // Act
        _toolManager.UpdateCategorySettings(ToolCategory.ReadFiles, false, ToolApprovalMode.Ask);

        // Assert
        var state = _toolManager.Current.CategoryStates[ToolCategory.ReadFiles];
        Assert.False(state.IsEnabled);
        Assert.Equal(ToolApprovalMode.Ask, state.ApprovalMode);
    }

    [Fact]
    public void UpdateCategorySettings_CreatesNewCategoryIfNotExists()
    {
        // Arrange
        _toolManager.RegisterAllTools();
        _toolManager.Current.CategoryStates.Remove(ToolCategory.Execution);

        // Act
        _toolManager.UpdateCategorySettings(ToolCategory.Execution, true, ToolApprovalMode.Deny);

        // Assert
        Assert.True(_toolManager.Current.CategoryStates.ContainsKey(ToolCategory.Execution));
        var state = _toolManager.Current.CategoryStates[ToolCategory.Execution];
        Assert.True(state.IsEnabled);
        Assert.Equal(ToolApprovalMode.Deny, state.ApprovalMode);
    }

    [Fact]
    public void GetEnabledTools_RespectsCategoryIsEnabled()
    {
        // Arrange
        var tool = new Tool
        {
            Name = "category_tool",
            Category = ToolCategory.Execution,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _builtInAgent.Tools = [tool];
        _toolManager.RegisterAllTools();
        _toolManager.Current.CategoryStates[ToolCategory.Execution] = new ToolCategorySettings
        {
            IsEnabled = false,
            ApprovalMode = ToolApprovalMode.Allow
        };

        // Act
        var enabledTools = _toolManager.GetEnabledTools(AppMode.Agent);

        // Assert
        Assert.DoesNotContain(enabledTools, t => t.Name == "category_tool");
    }

    [Fact]
    public void GetEnabledTools_ReturnsToolWhenCategoryEnabled()
    {
        // Arrange
        var tool = new Tool
        {
            Name = "category_tool",
            Category = ToolCategory.ReadFiles,
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _builtInAgent.Tools = [tool];
        _toolManager.RegisterAllTools();
        _toolManager.Current.CategoryStates[ToolCategory.ReadFiles] = new ToolCategorySettings
        {
            IsEnabled = true,
            ApprovalMode = ToolApprovalMode.Allow
        };

        // Act
        var enabledTools = _toolManager.GetEnabledTools(AppMode.Agent);

        // Assert
        Assert.Contains(enabledTools, t => t.Name == "category_tool");
    }

    [Fact]
    public void GetApprovalModeByToolName_ReturnsCategoryApprovalModeForBuiltInTool()
    {
        // Arrange
        var tool = new Tool
        {
            Name = "exec_tool",
            Category = ToolCategory.Execution,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _builtInAgent.Tools = [tool];
        _toolManager.RegisterAllTools();
        _toolManager.Current.CategoryStates[ToolCategory.Execution] = new ToolCategorySettings
        {
            ApprovalMode = ToolApprovalMode.Ask
        };

        // Act
        var mode = _toolManager.GetApprovalModeByToolName("exec_tool");

        // Assert
        Assert.Equal(ToolApprovalMode.Ask, mode);
    }

    [Fact]
    public void GetApprovalModeByToolName_ReturnsDefaultForUnknownTool()
    {
        // Arrange
        _toolManager.RegisterAllTools();

        // Act
        var mode = _toolManager.GetApprovalModeByToolName("unknown_tool");

        // Assert
        Assert.Equal(ToolApprovalMode.Allow, mode);
    }
}
