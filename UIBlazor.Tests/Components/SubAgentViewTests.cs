namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="SubAgentView"/>
/// </summary>
public class SubAgentViewTests : BunitContext
{
    private readonly Mock<IJSRuntime> _mockJsRuntime;

    public SubAgentViewTests()
    {
        _mockJsRuntime = new Mock<IJSRuntime>();
        Services.AddSingleton(_mockJsRuntime.Object);
        Services.AddRadzenComponents();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
        JSInterop.SetupVoid("scrollSubAgentToBottom", _ => true);

        // Stub MarkdownBlock to avoid JS interop for markdown rendering
        ComponentFactories.AddStub<MarkdownBlock>(parameters => builder =>
        {
            var content = parameters.Get(p => p.Content);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "markdown-block-stub");
            builder.AddContent(2, content ?? string.Empty);
            builder.CloseElement();
        });

        // Stub Details component to avoid complex child rendering,
        // but still render ChildContent so reasoning content is visible in tests
        ComponentFactories.AddStub<Details>(parameters => builder =>
        {
            var text = parameters.Get(p => p.Text);
            var childContent = parameters.Get(p => p.ChildContent);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "details-stub");
            builder.AddContent(2, text ?? string.Empty);
            if (childContent is not null)
            {
                builder.AddContent(3, childContent);
            }
            builder.CloseElement();
        });

        // Stub ToolCallBlock to avoid needing IToolManager and IToolCallHandler
        ComponentFactories.AddStub<ToolCallBlock>(parameters => builder =>
        {
            var toolCall = parameters.Get(p => p.ToolCall);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "toolcall-block-stub");
            builder.AddAttribute(2, "data-tool", toolCall?.Function?.Name ?? "unknown");
            builder.CloseElement();
        });
    }

    private static SubAgentMessage CreateSubAgent(
        SubAgentStatus status = SubAgentStatus.Pending,
        string task = "Test task",
        int totalTokens = 0,
        bool isExpanded = false,
        string? errorMessage = null)
    {
        return new SubAgentMessage
        {
            Status = status,
            Task = task,
            TotalTokens = totalTokens,
            IsExpanded = isExpanded,
            ErrorMessage = errorMessage
        };
    }

    private static VisualChatMessage CreateAssistantMessage(
        string content = "Assistant response",
        string? reasoningContent = null,
        List<ToolCall>? toolCalls = null)
    {
        return new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            Content = content,
            ReasoningContent = reasoningContent ?? string.Empty,
            ToolCalls = toolCalls,
            Timestamp = new DateTime(2024, 1, 15, 10, 30, 0)
        };
    }

    #region Status Badge Tests

    [Fact]
    public void ShouldRenderRunningBadge_WhenStatusRunning()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Running);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.Contains(SharedResource.SubAgentRunning, cut.Markup);
        var block = cut.Find(".subagent-block");
        Assert.Contains("status-running", block.ClassList);
    }

    [Fact]
    public void ShouldRenderCompletedBadge_WhenStatusCompleted()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.Contains(SharedResource.SubAgentCompleted, cut.Markup);
        var block = cut.Find(".subagent-block");
        Assert.Contains("status-completed", block.ClassList);
    }

    [Fact]
    public void ShouldRenderFailedBadge_WhenStatusFailed()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Failed);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.Contains(SharedResource.SubAgentFailed, cut.Markup);
        var block = cut.Find(".subagent-block");
        Assert.Contains("status-failed", block.ClassList);
    }

    [Fact]
    public void ShouldRenderCancelledBadge_WhenStatusCancelled()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Cancelled);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.Contains(SharedResource.SubAgentCancelled, cut.Markup);
        var block = cut.Find(".subagent-block");
        Assert.Contains("status-cancelled", block.ClassList);
    }

    [Fact]
    public void ShouldRenderPendingStatusClass_WhenStatusPending()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Pending);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert - Pending has no badge but has status class
        var block = cut.Find(".subagent-block");
        Assert.Contains("status-pending", block.ClassList);
    }

    #endregion

    #region Task Display Tests

    [Fact]
    public void ShouldRenderTask_InHeader()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, task: "Refactor authentication module");

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        var taskSpan = cut.Find(".subagent-task");
        Assert.Contains("Refactor authentication module", taskSpan.TextContent);
    }

    [Fact]
    public void ShouldTruncateLongTask_WithEllipsis()
    {
        // Arrange - task longer than 80 characters
        var longTask = new string('A', 100);
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, task: longTask);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        var taskSpan = cut.Find(".subagent-task");
        var text = taskSpan.TextContent;
        Assert.Contains("...", text);
        // Truncated text should be 80 chars + "..."
        Assert.Equal(83, text.Length);
    }

    [Fact]
    public void ShouldNotTruncateShortTask()
    {
        // Arrange - task shorter than 80 characters
        var shortTask = "Short task description";
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, task: shortTask);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        var taskSpan = cut.Find(".subagent-task");
        Assert.Equal(shortTask, taskSpan.TextContent);
    }

    [Fact]
    public void ShouldRenderEmptyTask_WhenTaskIsNull()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, task: null!);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert - should not crash, task span should be empty
        var taskSpan = cut.Find(".subagent-task");
        Assert.Equal(string.Empty, taskSpan.TextContent);
    }

    #endregion

    #region Token Display Tests

    [Fact]
    public void ShouldRenderTokens_WhenTotalTokensGreaterThanZero()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, totalTokens: 1500);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.Contains("1500", cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderTokens_WhenTotalTokensIsZero()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, totalTokens: 0);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert - token badge should not be present
        // The token badge has title="Tokens" attribute
        var tokenBadges = cut.FindAll("[title='Tokens']");
        Assert.Empty(tokenBadges);
    }

    #endregion

    #region Toggle (Expand/Collapse) Tests

    [Fact]
    public void ShouldNotRenderBody_WhenNotExpanded()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: false);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".subagent-body"));
    }

    [Fact]
    public void ShouldRenderBody_WhenExpanded()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: true);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.NotNull(cut.Find(".subagent-body"));
    }

    [Fact]
    public async Task Toggle_ClickExpandsBody()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: false);

        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert initial state - body not visible
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".subagent-body"));

        // Act - click header to expand
        var header = cut.Find(".subagent-header");
        await cut.InvokeAsync(() => header.Click());

        // Assert - body should now be visible
        Assert.NotNull(cut.Find(".subagent-body"));
        Assert.True(subAgent.IsExpanded);
    }

    [Fact]
    public async Task Toggle_ClickCollapsesBody()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: true);

        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert initial state - body visible
        Assert.NotNull(cut.Find(".subagent-body"));

        // Act - click header to collapse
        var header = cut.Find(".subagent-header");
        await cut.InvokeAsync(() => header.Click());

        // Assert - body should no longer be visible
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".subagent-body"));
        Assert.False(subAgent.IsExpanded);
    }

    [Fact]
    public async Task Toggle_ArrowRotates_WhenExpanded()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: false);

        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert initial state - arrow not expanded
        var arrow = cut.Find(".subagent-arrow");
        Assert.DoesNotContain("expanded", arrow.ClassList);

        // Act - click to expand
        var header = cut.Find(".subagent-header");
        await cut.InvokeAsync(() => header.Click());

        // Assert - arrow should have expanded class
        arrow = cut.Find(".subagent-arrow");
        Assert.Contains("expanded", arrow.ClassList);
    }

    #endregion

    #region Expanded Content - Messages Tests

    [Fact]
    public void ShouldRenderAssistantMessages_WhenExpanded()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: true);
        subAgent.AddMessage(CreateAssistantMessage(content: "I will analyze the code"));
        subAgent.AddMessage(CreateAssistantMessage(content: "Found 3 issues to fix"));

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        var messages = cut.FindAll(".subagent-message");
        Assert.Equal(2, messages.Count);
        Assert.Contains("I will analyze the code", cut.Markup);
        Assert.Contains("Found 3 issues to fix", cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderUserMessages_WhenExpanded()
    {
        // Arrange - user messages are skipped in the render loop
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: true);
        subAgent.AddMessage(new VisualChatMessage
        {
            Role = ChatMessageRole.User,
            Content = "User task message"
        });
        subAgent.AddMessage(CreateAssistantMessage(content: "Assistant response"));

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert - only assistant message should be rendered
        var messages = cut.FindAll(".subagent-message");
        Assert.Single(messages);
        Assert.DoesNotContain("User task message", cut.Markup);
    }

    [Fact]
    public void ShouldRenderEmptyConversation_WhenNoMessagesAndExpanded()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: true);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert - body should render with conversation container but no messages
        Assert.NotNull(cut.Find(".subagent-body"));
        Assert.NotNull(cut.Find(".subagent-conversation"));
        Assert.Empty(cut.FindAll(".subagent-message"));
    }

    [Fact]
    public void ShouldRenderReasoningContent_WhenExpandedAndPresent()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: true);
        subAgent.AddMessage(CreateAssistantMessage(
            content: "Here is my answer",
            reasoningContent: "Let me think about this problem"));

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert - reasoning content should be in a Details stub
        Assert.Contains("Let me think about this problem", cut.Markup);
    }

    #endregion

    #region Expanded Content - Tool Calls Tests

    [Fact]
    public void ShouldRenderToolCalls_WhenExpandedAndPresent()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: true);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction
            {
                Name = "read_files",
                Arguments = "{\"filePath\":\"test.cs\"}"
            }
        };
        subAgent.AddMessage(CreateAssistantMessage(
            content: "Reading file",
            toolCalls: [toolCall]));

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert - tool call block stub should be rendered
        var toolCallBlocks = cut.FindAll(".toolcall-block-stub");
        Assert.Single(toolCallBlocks);
        Assert.Equal("read_files", toolCallBlocks[0].GetAttribute("data-tool"));
    }

    [Fact]
    public void ShouldNotRenderToolCallsContainer_WhenNoToolCalls()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: true);
        subAgent.AddMessage(CreateAssistantMessage(content: "No tools needed"));

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".subagent-toolcalls"));
    }

    [Fact]
    public void ShouldRenderMultipleToolCalls_WhenPresent()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed, isExpanded: true);
        var toolCall1 = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" }
        };
        var toolCall2 = new ToolCall
        {
            Id = "tc-2",
            Function = new ToolCallFunction { Name = "grep_search", Arguments = "{}" }
        };
        subAgent.AddMessage(CreateAssistantMessage(
            content: "Using tools",
            toolCalls: [toolCall1, toolCall2]));

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        var toolCallBlocks = cut.FindAll(".toolcall-block-stub");
        Assert.Equal(2, toolCallBlocks.Count);
    }

    #endregion

    #region Error Display Tests

    [Fact]
    public void ShouldRenderErrorMessage_WhenFailedAndExpanded()
    {
        // Arrange
        var subAgent = CreateSubAgent(
            SubAgentStatus.Failed,
            isExpanded: true,
            errorMessage: "Connection timeout to API");

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.NotNull(cut.Find(".subagent-result-error"));
        Assert.Contains("Connection timeout to API", cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderErrorMessage_WhenFailedButNotExpanded()
    {
        // Arrange
        var subAgent = CreateSubAgent(
            SubAgentStatus.Failed,
            isExpanded: false,
            errorMessage: "Connection timeout to API");

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert - body not rendered, so error not visible
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".subagent-result-error"));
    }

    [Fact]
    public void ShouldNotRenderErrorMessage_WhenFailedButErrorMessageIsNull()
    {
        // Arrange
        var subAgent = CreateSubAgent(
            SubAgentStatus.Failed,
            isExpanded: true,
            errorMessage: null);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".subagent-result-error"));
    }

    [Fact]
    public void ShouldNotRenderErrorMessage_WhenCompletedAndExpanded()
    {
        // Arrange
        var subAgent = CreateSubAgent(
            SubAgentStatus.Completed,
            isExpanded: true,
            errorMessage: "Some old error");

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert - error block only shows for Failed status
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".subagent-result-error"));
    }

    #endregion

    #region Compressing Indicator Tests

    [Fact]
    public void ShouldRenderCompressingBadge_WhenIsCompressing()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Running);
        subAgent.IsCompressing = true;

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.Contains(SharedResource.Compressing, cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderCompressingBadge_WhenNotCompressing()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Running);
        subAgent.IsCompressing = false;

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        Assert.DoesNotContain(SharedResource.Compressing, cut.Markup);
    }

    #endregion

    #region Data Attribute Tests

    [Fact]
    public void ShouldRenderDataSubAgentId_Attribute()
    {
        // Arrange
        var subAgent = CreateSubAgent(SubAgentStatus.Completed);

        // Act
        var cut = Render<SubAgentView>(parameters => parameters
            .Add(p => p.SubAgent, subAgent));

        // Assert
        var block = cut.Find(".subagent-block");
        Assert.Equal(subAgent.Id, block.GetAttribute("data-subagent-id"));
    }

    #endregion
}
