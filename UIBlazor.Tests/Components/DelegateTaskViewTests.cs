namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="DelegateTaskView"/>.
/// After Phase 2c the view renders one info-block per delegated task (the 'tasks'
/// array), with a legacy fallback for the old single-task argument shape.
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

    #region Tasks Array Display Tests

    [Fact]
    public void ShouldRenderTaskFromTasksArray()
    {
        var args = """{"tasks":[{"task":"Refactor the authentication module"}]}""";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        var valueSpans = cut.FindAll(".subagent-info-value");
        Assert.Contains(valueSpans, s => s.TextContent.Contains("Refactor the authentication module"));
        Assert.Contains(SharedResource.SubAgentTask, cut.Markup);
    }

    [Fact]
    public void ShouldRenderOneBlockPerTask()
    {
        var args = """{"tasks":[{"task":"First"},{"task":"Second"},{"task":"Third"}]}""";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        // Task + AllowedTools rows for every task entry (no system prompt)
        var rows = cut.FindAll(".subagent-info-row");
        Assert.Equal(6, rows.Count);
        Assert.Contains("First", cut.Markup);
        Assert.Contains("Second", cut.Markup);
        Assert.Contains("Third", cut.Markup);
    }

    [Fact]
    public void ShouldRenderSystemPromptInsideTaskBlock()
    {
        var args = """{"tasks":[{"task":"Do something","systemPrompt":"You are a code reviewer"}]}""";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        Assert.Contains("You are a code reviewer", cut.Markup);
        Assert.Contains(SharedResource.SubAgentSystemPrompt, cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderSystemPromptRow_WhenSystemPromptIsEmpty()
    {
        var args = """{"tasks":[{"task":"Do something","systemPrompt":""}]}""";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        Assert.DoesNotContain(SharedResource.SubAgentSystemPrompt, cut.Markup);
    }

    [Fact]
    public void ShouldRenderAllToolsBadge_WhenAllowedToolsAbsent()
    {
        var args = """{"tasks":[{"task":"Do something"}]}""";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        Assert.Contains(SharedResource.SubAgentAllTools, cut.Markup);
    }

    [Fact]
    public void ShouldRenderAllToolsBadge_WhenAllowedToolsIsEmptyArray()
    {
        var args = """{"tasks":[{"task":"Do something","allowedTools":[]}]}""";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        Assert.Contains(SharedResource.SubAgentAllTools, cut.Markup);
    }

    [Fact]
    public void ShouldRenderSpecificToolBadges_WhenAllowedToolsProvided()
    {
        var args = """{"tasks":[{"task":"Do something","allowedTools":["read_files","grep_search"]}]}""";

        _mockToolManager
            .Setup(x => x.GetTool("read_files"))
            .Returns(CreateTool("read_files", "Read Files"));

        _mockToolManager
            .Setup(x => x.GetTool("grep_search"))
            .Returns(CreateTool("grep_search", "Grep Search"));

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        Assert.Contains("Read Files", cut.Markup);
        Assert.Contains("Grep Search", cut.Markup);
        Assert.DoesNotContain(SharedResource.SubAgentAllTools, cut.Markup);
    }

    [Fact]
    public void ShouldRenderToolNameAsFallback_WhenToolNotFound()
    {
        var args = """{"tasks":[{"task":"Do something","allowedTools":["unknown_tool"]}]}""";

        _mockToolManager
            .Setup(x => x.GetTool("unknown_tool"))
            .Returns((Tool?)null);

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        Assert.Contains("unknown_tool", cut.Markup);
    }

    #endregion

    #region Legacy Single-Task Fallback Tests

    [Fact]
    public void ShouldRenderLegacySingleTask_AsFallback()
    {
        var args = """{"task":"Legacy task","systemPrompt":"Legacy prompt","allowedTools":["read_files"]}""";

        _mockToolManager
            .Setup(x => x.GetTool("read_files"))
            .Returns(CreateTool("read_files", "Read Files"));

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        Assert.Contains("Legacy task", cut.Markup);
        Assert.Contains("Legacy prompt", cut.Markup);
        Assert.Contains("Read Files", cut.Markup);

        var rows = cut.FindAll(".subagent-info-row");
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void ShouldPreferTasksArray_OverLegacyFields()
    {
        var args = """{"tasks":[{"task":"Array task"}],"task":"Legacy task"}""";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        Assert.Contains("Array task", cut.Markup);
        Assert.DoesNotContain("Legacy task", cut.Markup);
    }

    #endregion

    #region Empty/Null Args Tests

    [Fact]
    public void ShouldRenderEmptyContainer_WhenArgsIsEmpty()
    {
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, string.Empty));

        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Empty(cut.FindAll(".subagent-info-row"));
    }

    [Fact]
    public void ShouldRenderEmptyContainer_WhenArgsIsNull()
    {
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, null!));

        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Empty(cut.FindAll(".subagent-info-row"));
    }

    [Fact]
    public void ShouldRenderEmptyContainer_WhenArgsIsWhitespace()
    {
        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, "   "));

        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Empty(cut.FindAll(".subagent-info-row"));
    }

    #endregion

    #region Invalid JSON Tests

    [Fact]
    public void ShouldRenderEmptyContainer_WhenArgsIsInvalidJson()
    {
        var invalidJson = "this is not valid json {{{";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, invalidJson));

        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Empty(cut.FindAll(".subagent-info-row"));
    }

    [Fact]
    public void ShouldRenderEmptyContainer_WhenArgsIsPartialJson()
    {
        var partialJson = """{"task":"Partial task""";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, partialJson));

        // The view intentionally does not repair partial JSON; it renders nothing
        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Empty(cut.FindAll(".subagent-info-row"));
    }

    [Fact]
    public void ShouldRenderEmptyContainer_WhenArgsIsRandomText()
    {
        var randomText = "random text without any json structure";

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, randomText));

        Assert.NotNull(cut.Find(".subagent-info"));
        Assert.Empty(cut.FindAll(".subagent-info-row"));
    }

    #endregion

    #region Full Args Rendering Tests

    [Fact]
    public void ShouldRenderAllSections_WhenFullArgsProvided()
    {
        var args = """{"tasks":[{"task":"Implement feature X","systemPrompt":"You are an expert developer","allowedTools":["read_files","edit_files"]}]}""";

        _mockToolManager
            .Setup(x => x.GetTool("read_files"))
            .Returns(CreateTool("read_files", "Read Files"));

        _mockToolManager
            .Setup(x => x.GetTool("edit_files"))
            .Returns(CreateTool("edit_files", "Edit File"));

        var cut = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args));

        Assert.Contains("Implement feature X", cut.Markup);
        Assert.Contains("You are an expert developer", cut.Markup);
        Assert.Contains("Read Files", cut.Markup);
        Assert.Contains("Edit File", cut.Markup);
        Assert.Contains(SharedResource.SubAgentTask, cut.Markup);
        Assert.Contains(SharedResource.SubAgentSystemPrompt, cut.Markup);
        Assert.Contains(SharedResource.SubAgentAllowedTools, cut.Markup);

        var rows = cut.FindAll(".subagent-info-row");
        Assert.Equal(3, rows.Count);
    }

    #endregion

    #region Parameter Update Tests

    [Fact]
    public void ShouldRenderCorrectTask_WithDifferentArgs()
    {
        // Arrange - render with first args
        var args1 = """{"tasks":[{"task":"First task"}]}""";
        var cut1 = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args1));

        // Assert first render
        Assert.Contains("First task", cut1.Markup);

        // Act - render a new component with different args
        var args2 = """{"tasks":[{"task":"Second task"}]}""";
        var cut2 = Render<DelegateTaskView>(parameters => parameters
            .Add(p => p.Args, args2));

        // Assert second render shows the new task
        Assert.Contains("Second task", cut2.Markup);
    }

    #endregion
}
