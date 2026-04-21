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

public class ToolManagerTests
{
    private readonly ToolManager _toolManager;
    private readonly BuiltInAgent _builtInAgent;
    private readonly Mock<ILocalStorageService> _localStorageMock;
    private readonly Mock<IMcpSettingsProvider> _mcpSettingsMock;
    private readonly Mock<IVsBridge> _vsBridgeMock;
    private readonly McpOptions _mcpOptions;
    private readonly ILogger<ToolManager> _logger;

    public ToolManagerTests()
    {
        _localStorageMock = new Mock<ILocalStorageService>();
        _mcpSettingsMock = new Mock<IMcpSettingsProvider>();
        _vsBridgeMock = new Mock<IVsBridge>();
        _logger = new LoggerMock<ToolManager>();

        _mcpOptions = new McpOptions { Enabled = true };
        _mcpSettingsMock.Setup(m => m.Current).Returns(_mcpOptions);

        // Setup default tool
        var tool = new Tool
        {
            Name = "test_tool",
            Description = "Test tool",
            Category = ToolCategory.ReadFiles,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "test result" })
        };
        _builtInAgent = new BuiltInAgent(_vsBridgeMock.Object, Mock.Of<ISkillService>(), Mock.Of<IInternalExecutor>()) { Tools = [tool] };

        _toolManager = new ToolManager(_builtInAgent, _logger, _localStorageMock.Object, _mcpSettingsMock.Object, _vsBridgeMock.Object);
    }

    [Fact]
    public void RegisterAllTools_RegistersToolsFromAgent()
    {
        // Arrange
        var tool1 = new Tool { Name = "tool1", ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "result1" }) };
        var tool2 = new Tool { Name = "tool2", ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "result2" }) };
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

    #region UpdateCategorySettings Tests

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

    #endregion

    #region ToggleTool Tests

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

    #endregion

    #region SaveAsync Tests

    [Fact]
    public async Task SaveAsync_UpdatesCategoryStatesForNewCategories()
    {
        // Arrange
        var tool1 = new Tool { Name = "tool1", Category = ToolCategory.Execution, ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true }) };
        var tool2 = new Tool { Name = "tool2", Category = ToolCategory.DeleteFiles, ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true }) };
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

    #endregion

    #region ResetAsync Tests

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

    #endregion

    #region GetEnabledTools with Category Tests

    [Fact]
    public void GetEnabledTools_RespectsCategoryIsEnabled()
    {
        // Arrange
        var tool = new Tool
        {
            Name = "category_tool",
            Category = ToolCategory.Execution,
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
        var enabledTools = _toolManager.GetEnabledTools();

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
        var enabledTools = _toolManager.GetEnabledTools();

        // Assert
        Assert.Contains(enabledTools, t => t.Name == "category_tool");
    }

    #endregion

    #region GetApprovalModeByToolName Tests for Built-in Tools

    [Fact]
    public void GetApprovalModeByToolName_ReturnsCategoryApprovalModeForBuiltInTool()
    {
        // Arrange
        var tool = new Tool
        {
            Name = "exec_tool",
            Category = ToolCategory.Execution,
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

    #endregion

    #region GetToolUseSystemInstructions Mode Tests

    [Fact]
    public void GetToolUseSystemInstructions_IncludesCurrentDate()
    {
        // Arrange
        _toolManager.RegisterAllTools();

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Agent, false);

        // Assert
        Assert.Contains("Current date:", instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_IncludesCurrentMode()
    {
        // Arrange
        _toolManager.RegisterAllTools();

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Agent, false);

        // Assert
        Assert.Contains("Your current mode:", instructions);
        Assert.Contains("Agent", instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_IncludesOtherModes_WhenModeSwitchToolEnabled()
    {
        // Arrange - create ToolManager with switch_mode tool enabled
        var modeSwitchTool = new Tool
        {
            Name = BasicEnum.SwitchMode,
            Category = ToolCategory.ModeSwitch,
            Enabled = true,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        var agent = new BuiltInAgent(_vsBridgeMock.Object, Mock.Of<ISkillService>(), Mock.Of<IInternalExecutor>()) { Tools = [modeSwitchTool] };

        // Setup storage to return settings with ModeSwitch category enabled
        var savedSettings = new ToolSettings();
        savedSettings.CategoryStates[ToolCategory.ModeSwitch] = new ToolCategorySettings
        {
            IsEnabled = true,
            ApprovalMode = ToolApprovalMode.Allow
        };
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ToolSettings>("ToolSettings"))
            .ReturnsAsync(savedSettings);

        var toolManager = new ToolManager(agent, _logger, _localStorageMock.Object, _mcpSettingsMock.Object, _vsBridgeMock.Object);
        toolManager.RegisterAllTools();

        // Act
        var instructions = toolManager.GetToolUseSystemInstructions(AppMode.Agent, false);

        // Assert
        Assert.Contains("Other available modes:", instructions);
        Assert.Contains("Chat", instructions);
        Assert.Contains("Plan", instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_ExcludesOtherModes_WhenModeSwitchToolDisabled()
    {
        // Arrange - create ToolManager with switch_mode tool disabled
        var modeSwitchTool = new Tool
        {
            Name = BasicEnum.SwitchMode,
            Category = ToolCategory.ModeSwitch,
            Enabled = false,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        var agent = new BuiltInAgent(_vsBridgeMock.Object, Mock.Of<ISkillService>(), Mock.Of<IInternalExecutor>()) { Tools = [modeSwitchTool] };
        var toolManager = new ToolManager(agent, _logger, _localStorageMock.Object, _mcpSettingsMock.Object, _vsBridgeMock.Object);

        // Act
        var instructions = toolManager.GetToolUseSystemInstructions(AppMode.Agent, false);

        // Assert
        Assert.DoesNotContain("Other available modes:", instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_PlanModeIncludesPlanningInstructions()
    {
        // Arrange
        _toolManager.RegisterAllTools();

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Plan, false);

        // Assert
        Assert.Contains("Planning Mode Instructions", instructions);
        Assert.Contains("<plan>", instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_ChatModeFiltersTools()
    {
        // Arrange
        var readFileTool = new Tool
        {
            Name = "read_tool",
            Category = ToolCategory.ReadFiles,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        var writeFileTool = new Tool
        {
            Name = "write_tool",
            Category = ToolCategory.WriteFiles,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _builtInAgent.Tools = [readFileTool, writeFileTool];
        _toolManager.RegisterAllTools();

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Chat, false);

        // Assert
        Assert.Contains("read_tool", instructions);
        Assert.DoesNotContain("write_tool", instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_FiltersReadSkillContentWhenNoSkills()
    {
        // Arrange
        var skillTool = new Tool
        {
            Name = BasicEnum.ReadSkillContent,
            Category = ToolCategory.ReadFiles,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _builtInAgent.Tools = [skillTool];
        _toolManager.RegisterAllTools();

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Chat, false);

        // Assert
        Assert.DoesNotContain(BasicEnum.ReadSkillContent, instructions);
    }

    [Fact]
    public void GetToolUseSystemInstructions_IncludesReadSkillContentWhenHasSkills()
    {
        // Arrange
        var skillTool = new Tool
        {
            Name = BasicEnum.ReadSkillContent,
            Category = ToolCategory.ReadFiles,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _builtInAgent.Tools = [skillTool];
        _toolManager.RegisterAllTools();

        // Act
        var instructions = _toolManager.GetToolUseSystemInstructions(AppMode.Chat, true);

        // Assert
        Assert.Contains(BasicEnum.ReadSkillContent, instructions);
    }

    #endregion

    #region McpSettingsProvider OnSaved Tests

    [Fact]
    public void McpSettingsProviderOnSaved_ClearsMcpToolsCache()
    {
        // Arrange
        var server = new McpServerConfig
        {
            Name = "test-server",
            Enabled = true,
            Tools = [new McpToolConfig { Name = "test-tool", Description = "Test" }]
        };
        _mcpOptions.Servers.Add(server);
        _toolManager.RegisterAllTools();

        // First call to populate cache
        var firstCallTools = _toolManager.GetMcpTools().ToList();
        Assert.Single(firstCallTools);

        // Add new tool to server
        server.Tools.Add(new McpToolConfig { Name = "new-tool", Description = "New" });

        // Act - trigger OnSaved event
        _mcpSettingsMock.Raise(m => m.OnSaved += null);

        // Assert - cache should be cleared, new tool should appear
        var secondCallTools = _toolManager.GetMcpTools().ToList();
        Assert.Equal(2, secondCallTools.Count);
    }

    #endregion

    #region InitializeAsync Tests

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

    #endregion
}
