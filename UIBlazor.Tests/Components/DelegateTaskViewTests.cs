namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="DelegateTaskView"/>
/// </summary>
public class DelegateTaskViewTests : BunitContext
{
    private readonly Mock<IToolManager> _mockToolManager;

    public DelegateTaskViewTests()
    {
        _mockToolManager = new Mock<IToolManager>();
        Services.AddSingleton(_mockToolManager.Object);
        Services.AddRadzenComponents();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
    }

    private static Tool CreateTool(string name, string displayName)
    {
        return new Tool
        {
            Name = name,
            DisplayName = displayName,
            NativeTool = new NativeToolDefinition
            {
                Function = new NativeToolFunction
                {
                    Name = name,
                    Description = displayName,
                    Parameters = new NativeParameters
                    {
                        Type = NativeToolType.String,
                        Properties = []
                    }
                }
            },
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult())
        };
    }

    #region Task Display Tests

    [Fact]
    public void ShouldRenderTask_WhenArgsContainTask()
    {
        // Arrange
        var args = """{"task":"Refactor the authentication module"}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        var valueSpans = cut.FindAll(".subagent-info-value");
        Assert.Contains(valueSpans, s => s.TextContent.Contains("Refactor the authentication module"));
    }

    [Fact]
    public void ShouldRenderTaskLabel()
    {
        // Arrange
        var args = """{"task":"Some task"}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        Assert.Contains(SharedResource.SubAgentTask, cut.Markup);
    }

    [Fact]
    public void ShouldRenderEmptyTask_WhenTaskNotInArgs()
    {
        // Arrange
        var args = """{"systemPrompt":"Some prompt"}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert - should render without crash, task value empty
        var rows = cut.FindAll(".subagent-info-row");
        Assert.NotEmpty(rows);
        var firstRowValue = rows[0].QuerySelector(".subagent-info-value");
        Assert.Equal(string.Empty, firstRowValue?.TextContent);
    }

    #endregion

    #region System Prompt Display Tests

    [Fact]
    public void ShouldRenderSystemPrompt_WhenArgsContainSystemPrompt()
    {
        // Arrange
        var args = """{"task":"Do something","systemPrompt":"You are a code reviewer"}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        Assert.Contains("You are a code reviewer", cut.Markup);
        Assert.Contains(SharedResource.SubAgentSystemPrompt, cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderSystemPromptRow_WhenSystemPromptIsEmpty()
    {
        // Arrange
        var args = """{"task":"Do something","systemPrompt":""}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert - system prompt row should not be rendered (empty string check)
        Assert.DoesNotContain(SharedResource.SubAgentSystemPrompt, cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderSystemPromptRow_WhenSystemPromptIsNull()
    {
        // Arrange
        var args = """{"task":"Do something"}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        Assert.DoesNotContain(SharedResource.SubAgentSystemPrompt, cut.Markup);
    }

    [Fact]
    public void ShouldRenderSystemPromptRow_WithLongClass()
    {
        // Arrange
        var args = """{"task":"Task","systemPrompt":"Long prompt here"}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        var longValue = cut.Find(".subagent-info-value--long");
        Assert.NotNull(longValue);
        Assert.Contains("Long prompt here", longValue.TextContent);
    }

    #endregion

    #region Allowed Tools Display Tests

    [Fact]
    public void ShouldRenderAllToolsBadge_WhenAllowedToolsIsNull()
    {
        // Arrange
        var args = """{"task":"Do something"}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        Assert.Contains(SharedResource.SubAgentAllTools, cut.Markup);
    }

    [Fact]
    public void ShouldRenderAllToolsBadge_WhenAllowedToolsIsEmptyArray()
    {
        // Arrange
        var args = """{"task":"Do something","allowedTools":[]}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        Assert.Contains(SharedResource.SubAgentAllTools, cut.Markup);
    }

    [Fact]
    public void ShouldRenderSpecificToolBadges_WhenAllowedToolsProvided()
    {
        // Arrange
        var args = """{"task":"Do something","allowedTools":["read_files","grep_search"]}""";

        _mockToolManager
            .Setup(x => x.GetTool("read_files"))
            .Returns(CreateTool("read_files", "Read Files"));

        _mockToolManager
            .Setup(x => x.GetTool("grep_search"))
            .Returns(CreateTool("grep_search", "Grep Search"));

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        Assert.Contains("Read Files", cut.Markup);
        Assert.Contains("Grep Search", cut.Markup);
        Assert.DoesNotContain(SharedResource.SubAgentAllTools, cut.Markup);
    }

    [Fact]
    public void ShouldRenderToolNameAsFallback_WhenToolNotFound()
    {
        // Arrange
        var args = """{"task":"Do something","allowedTools":["unknown_tool"]}""";

        _mockToolManager
            .Setup(x => x.GetTool("unknown_tool"))
            .Returns((Tool?)null);

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert - should display the raw tool name when ToolManager returns null
        Assert.Contains("unknown_tool", cut.Markup);
    }

    [Fact]
    public void ShouldRenderAllowedToolsLabel()
    {
        // Arrange
        var args = """{"task":"Do something"}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        Assert.Contains(SharedResource.SubAgentAllowedTools, cut.Markup);
    }

    #endregion

    #region Empty/Null Args Tests

    [Fact]
    public void ShouldNotCrash_WhenArgsIsEmpty()
    {
        // Arrange & Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, string.Empty));

        // Assert - should render with empty task and "All Tools" badge
        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Contains(SharedResource.SubAgentAllTools, cut.Markup);
    }

    [Fact]
    public void ShouldNotCrash_WhenArgsIsNull()
    {
        // Arrange & Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, null!));

        // Assert - should render without crash
        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Contains(SharedResource.SubAgentAllTools, cut.Markup);
    }

    [Fact]
    public void ShouldNotCrash_WhenArgsIsWhitespace()
    {
        // Arrange & Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, "   "));

        // Assert
        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Contains(SharedResource.SubAgentAllTools, cut.Markup);
    }

    #endregion

    #region Invalid JSON Tests

    [Fact]
    public void ShouldNotCrash_WhenArgsIsInvalidJson()
    {
        // Arrange
        var invalidJson = "this is not valid json {{{";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, invalidJson));

        // Assert - should render without crash, showing empty task and All Tools
        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Contains(SharedResource.SubAgentAllTools, cut.Markup);
    }

    [Fact]
    public void ShouldNotCrash_WhenArgsIsPartialJson()
    {
        // Arrange - partial JSON that gets repaired by JsonUtils
        var partialJson = """{"task":"Partial task""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, partialJson));

        // Assert - should render without crash
        Assert.NotNull(cut.Find(".subagent-info"));
        // JsonUtils.RepairJson should fix the partial JSON and extract the task
        Assert.Contains("Partial task", cut.Markup);
    }

    [Fact]
    public void ShouldNotCrash_WhenArgsIsRandomText()
    {
        // Arrange
        var randomText = "random text without any json structure";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, randomText));

        // Assert - should render without crash
        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Contains(SharedResource.SubAgentAllTools, cut.Markup);
    }

    #endregion

    #region Full Args Rendering Tests

    [Fact]
    public void ShouldRenderAllSections_WhenFullArgsProvided()
    {
        // Arrange
        var args = """{"task":"Implement feature X","systemPrompt":"You are an expert developer","allowedTools":["read_files","edits"]}""";

        _mockToolManager
            .Setup(x => x.GetTool("read_files"))
            .Returns(CreateTool("read_files", "Read Files"));

        _mockToolManager
            .Setup(x => x.GetTool("edits"))
            .Returns(CreateTool("edits", "Apply diff"));

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert
        Assert.Contains("Implement feature X", cut.Markup);
        Assert.Contains("You are an expert developer", cut.Markup);
        Assert.Contains("Read Files", cut.Markup);
        Assert.Contains("Apply diff", cut.Markup);
        Assert.Contains(SharedResource.SubAgentTask, cut.Markup);
        Assert.Contains(SharedResource.SubAgentSystemPrompt, cut.Markup);
        Assert.Contains(SharedResource.SubAgentAllowedTools, cut.Markup);
    }

    [Fact]
    public void ShouldRenderCorrectNumberOfInfoRows_WithTaskAndAllTools()
    {
        // Arrange - task present, no system prompt, no allowed tools
        var args = """{"task":"Simple task"}""";

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert - should have 2 rows: Task and Allowed Tools (no System Prompt)
        var rows = cut.FindAll(".subagent-info-row");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ShouldRenderCorrectNumberOfInfoRows_WithAllSections()
    {
        // Arrange - all sections present
        var args = """{"task":"Task","systemPrompt":"Prompt","allowedTools":["read_files"]}""";

        _mockToolManager
            .Setup(x => x.GetTool(It.IsAny<string>()))
            .Returns(CreateTool("read_files", "Read Files"));

        // Act
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Assert - should have 3 rows: Task, System Prompt, Allowed Tools
        var rows = cut.FindAll(".subagent-info-row");
        Assert.Equal(3, rows.Count);
    }

    #endregion

    #region Parameter Update Tests

    [Fact]
    public void ShouldRenderCorrectTask_WithDifferentArgs()
    {
        // Arrange - render with first args
        var args1 = """{"task":"First task"}""";
        var cut1 = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args1));

        // Assert first render
        Assert.Contains("First task", cut1.Markup);

        // Act - render a new component with different args
        var args2 = """{"task":"Second task"}""";
        var cut2 = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args2));

        // Assert second render shows the new task
        Assert.Contains("Second task", cut2.Markup);
    }

    #endregion
}
