namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="MessageContent"/>
/// </summary>
public class MessageContentTests : BunitContext
{
    private readonly Mock<IToolManager> _mockToolManager;

    public MessageContentTests()
    {
        _mockToolManager = new Mock<IToolManager>();
        Services.AddSingleton(_mockToolManager.Object);
        Services.AddRadzenComponents();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);

        // Stub MarkdownBlock to avoid JS interop for markdown rendering
        ComponentFactories.AddStub<MarkdownBlock>(parameters => builder =>
        {
            var content = parameters.Get(p => p.Content);
            var streaming = parameters.Get(p => p.IsStreaming);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "markdown-block-stub");
            builder.AddAttribute(2, "data-streaming", streaming ? "true" : "false");
            builder.AddContent(3, content ?? string.Empty);
            builder.CloseElement();
        });

        // Stub Details but keep child content visible
        ComponentFactories.AddStub<Details>(parameters => builder =>
        {
            var text = parameters.Get(p => p.Text);
            var icon = parameters.Get(p => p.Icon);
            var childContent = parameters.Get(p => p.ChildContent);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "details-stub");
            builder.AddAttribute(2, "data-icon", icon);
            builder.AddContent(3, text ?? string.Empty);
            if (childContent is not null)
            {
                builder.AddContent(4, childContent);
            }
            builder.CloseElement();
        });

        // Stub ToolCallBlock with a button that triggers OnApproval
        ComponentFactories.AddStub<ToolCallBlock>(parameters => builder =>
        {
            var toolCall = parameters.Get(p => p.ToolCall);
            var onApproval = parameters.Get(p => p.OnApproval);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "toolcall-block-stub");
            builder.AddAttribute(2, "data-toolcall-id", toolCall?.Id);
            builder.OpenElement(3, "button");
            builder.AddAttribute(4, "class", "stub-approve");
            builder.AddAttribute(5, "onclick",
                EventCallback.Factory.Create(this, () => onApproval.InvokeAsync((toolCall!.Id, true))));
            builder.AddContent(6, "approve");
            builder.CloseElement();
            builder.OpenElement(7, "button");
            builder.AddAttribute(8, "class", "stub-reject");
            builder.AddAttribute(9, "onclick",
                EventCallback.Factory.Create(this, () => onApproval.InvokeAsync((toolCall!.Id, false))));
            builder.AddContent(10, "reject");
            builder.CloseElement();
            builder.CloseElement();
        });
    }

    private static VisualChatMessage CreateUserMessage(string content = "Hello")
    {
        return new VisualChatMessage { Role = ChatMessageRole.User, Content = content };
    }

    private static VisualChatMessage CreateAssistantMessage(
        string content = "",
        string? reasoning = null,
        List<ToolCall>? toolCalls = null)
    {
        return new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            Content = content,
            ReasoningContent = reasoning ?? string.Empty,
            ToolCalls = toolCalls
        };
    }

    #region User / Non-Assistant Messages

    [Fact]
    public void UserMessage_RendersSingleMarkdownBlock_WithFullContent()
    {
        // Arrange
        var message = CreateUserMessage("User question text");

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert - single stub containing message content
        var blocks = cut.FindAll(".markdown-block-stub");
        Assert.Single(blocks);
        Assert.Contains("User question text", blocks[0].TextContent);
    }

    [Theory]
    [InlineData(ChatMessageRole.User)]
    [InlineData(ChatMessageRole.System)]
    [InlineData(ChatMessageRole.Tool)]
    public void NonAssistantRole_RendersPlainMarkdown(string role)
    {
        // Arrange
        var message = new VisualChatMessage { Role = role, Content = $"content of {role}" };

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert - no reasoning details, no tool call blocks
        Assert.Single(cut.FindAll(".markdown-block-stub"));
        Assert.Empty(cut.FindAll(".details-stub"));
        Assert.Empty(cut.FindAll(".toolcall-block-stub"));
    }

    #endregion

    #region Reasoning Tests

    [Fact]
    public void AssistantWithoutReasoning_DoesNotRenderReasoningDetails()
    {
        // Arrange
        var message = CreateAssistantMessage(content: "Answer");

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".details-stub"));
    }

    [Fact]
    public void AssistantWithReasoning_RendersReasoningDetails_WithThinkingHeader()
    {
        // Arrange
        var message = CreateAssistantMessage(reasoning: "Deep thought process");

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var details = cut.Find(".details-stub");
        Assert.Contains(SharedResource.Thinking, details.TextContent);
        Assert.Equal("fa-solid fa-lightbulb", details.GetAttribute("data-icon"));
        var innerBlock = details.QuerySelector(".markdown-block-stub");
        Assert.NotNull(innerBlock);
        Assert.Contains("Deep thought process", innerBlock.TextContent);
    }

    [Fact]
    public void AssistantWithLongReasoning_ShowsSecondsInHeader()
    {
        // Arrange - reasoning > 100 ms must include seconds
        var message = CreateAssistantMessage(reasoning: "Slow reasoning");
        message.Timings = new MessageTimings { Reasoning = TimeSpan.FromMilliseconds(2500) };

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var details = cut.Find(".details-stub");
        Assert.Contains("2.5 sec", details.TextContent);
        Assert.Contains(SharedResource.Thinking, details.TextContent);
    }

    [Fact]
    public void AssistantWithShortReasoningTiming_ShowsPlainHeader()
    {
        // Arrange - reasoning <= 100 ms must NOT include seconds
        var message = CreateAssistantMessage(reasoning: "Quick reasoning");
        message.Timings = new MessageTimings { Reasoning = TimeSpan.FromMilliseconds(50) };

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var details = cut.Find(".details-stub");
        Assert.DoesNotContain("sec", details.TextContent);
    }

    #endregion

    #region Segments Tests

    [Fact]
    public void AssistantSegments_RenderAsSeparateMarkdownBlocks()
    {
        // Arrange - ContentSegment.Type has internal setter; use reflection
        var segment1 = new ContentSegment();
        typeof(ContentSegment).GetProperty(nameof(ContentSegment.Type))!
            .SetValue(segment1, SegmentType.Markdown);
        segment1.CurrentLine.Append("First segment line");

        var segment2 = new ContentSegment();
        typeof(ContentSegment).GetProperty(nameof(ContentSegment.Type))!
            .SetValue(segment2, SegmentType.Markdown);
        segment2.CurrentLine.Append("Second segment line");

        var message = CreateAssistantMessage();
        message.Segments.Add(segment1);
        message.Segments.Add(segment2);

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var blocks = cut.FindAll(".markdown-block-stub");
        Assert.Equal(2, blocks.Count);
        Assert.Contains("First segment line", blocks[0].TextContent);
        Assert.Contains("Second segment line", blocks[1].TextContent);
    }

    /// <summary>
    /// Only the LAST markdown segment is still growing. Handing <c>IsStreaming=true</c> to a
    /// CLOSED segment would freeze it with up to one render-gate of text missing: its text stops
    /// changing, so the gate never opens again and the trailing characters are never rendered.
    /// </summary>
    [Fact]
    public void ClosedSegment_IsNotMarkedStreaming_LastOneIs()
    {
        var closed = new ContentSegment();
        typeof(ContentSegment).GetProperty(nameof(ContentSegment.Type))!
            .SetValue(closed, SegmentType.Markdown);
        closed.CurrentLine.Append("closed segment");
        closed.IsClosed = true;

        var growing = new ContentSegment();
        typeof(ContentSegment).GetProperty(nameof(ContentSegment.Type))!
            .SetValue(growing, SegmentType.Markdown);
        growing.CurrentLine.Append("still streaming");

        var message = CreateAssistantMessage();
        message.IsStreaming = true;
        message.Segments.Add(closed);
        message.Segments.Add(growing);

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert - the closed block must render immediately, the growing one may gate.
        var streamingFlags = cut.FindAll(".markdown-block-stub")
            .Select(e => e.GetAttribute("data-streaming") == "true")
            .ToList();

        Assert.Equal(2, streamingFlags.Count);
        Assert.False(streamingFlags[0], "a closed segment never receives more text - gating it would lose its tail");
        Assert.True(streamingFlags[1], "the last markdown segment is the one still growing");
    }

    /// <summary>
    /// A finished message is never treated as streaming, so every change renders.
    /// </summary>
    [Fact]
    public void FinishedMessage_SegmentsAreNotMarkedStreaming()
    {
        var segment = new ContentSegment();
        typeof(ContentSegment).GetProperty(nameof(ContentSegment.Type))!
            .SetValue(segment, SegmentType.Markdown);
        segment.CurrentLine.Append("done");

        var message = CreateAssistantMessage();
        message.IsStreaming = false;
        message.Segments.Add(segment);

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        Assert.All(cut.FindAll(".markdown-block-stub"),
            e => Assert.Equal("false", e.GetAttribute("data-streaming")));
    }

    #endregion

    #region Content fallback (no segments)

    /// <summary>
    /// The compression summary is Content-only in the model unless the compression core builds
    /// its segments. A message without segments used to render NOTHING (empty bubble that stayed
    /// invisible) even though it carried real text.
    /// </summary>
    [Fact]
    public void AssistantWithoutSegments_RendersContent()
    {
        // Arrange
        var message = CreateAssistantMessage(content: "## Goal\n- fix compression");

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var block = Assert.Single(cut.FindAll(".markdown-block-stub"));
        Assert.Contains("fix compression", block.TextContent);
    }

    [Fact]
    public void AssistantWithSegments_DoesNotRenderContentTwice()
    {
        // Arrange: segments are the render source when present — the raw Content must not be
        // rendered on top of them (the streamed text would appear twice).
        var segment = new ContentSegment();
        typeof(ContentSegment).GetProperty(nameof(ContentSegment.Type))!
            .SetValue(segment, SegmentType.Markdown);
        segment.CurrentLine.Append("segmented text");

        var message = CreateAssistantMessage(content: "segmented text");
        message.Segments.Add(segment);

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Single(cut.FindAll(".markdown-block-stub"));
    }

    #endregion

    #region Tool Calls Tests

    [Fact]
    public void AssistantToolCalls_RenderToolCallBlocks()
    {
        // Arrange
        var toolCall1 = new ToolCall { Id = "call-1" };
        var toolCall2 = new ToolCall { Id = "call-2" };
        var message = CreateAssistantMessage(toolCalls: [toolCall1, toolCall2]);

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var blocks = cut.FindAll(".toolcall-block-stub");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("call-1", blocks[0].GetAttribute("data-toolcall-id"));
        Assert.Equal("call-2", blocks[1].GetAttribute("data-toolcall-id"));
    }

    [Fact]
    public void NoToolCalls_DoesNotRenderToolCallBlocks()
    {
        // Arrange
        var message = CreateAssistantMessage(content: "plain answer", toolCalls: null);

        // Act
        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Empty(cut.FindAll(".toolcall-block-stub"));
    }

    #endregion

    #region Approval Callback Tests

    [Fact]
    public async Task ToolApproval_PassesMessageId_SegmentIdAndApprovedFlag()
    {
        // Arrange
        var message = CreateAssistantMessage(toolCalls: [new ToolCall { Id = "call-42" }]);
        var received = default((string MessageId, string SegmentId, bool Approved));

        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.OnToolApproval, EventCallback.Factory.Create<(string, string, bool)>(
                this, args => received = args)));

        // Act
        await cut.InvokeAsync(() => cut.Find(".stub-approve").Click());

        // Assert
        Assert.Equal((message.Id, "call-42", true), received);
    }

    [Fact]
    public async Task ToolRejection_PassesApprovedFalse()
    {
        // Arrange
        var message = CreateAssistantMessage(toolCalls: [new ToolCall { Id = "call-7" }]);
        var received = default((string MessageId, string SegmentId, bool Approved));

        var cut = Render<MessageContent>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.OnToolApproval, EventCallback.Factory.Create<(string, string, bool)>(
                this, args => received = args)));

        // Act
        await cut.InvokeAsync(() => cut.Find(".stub-reject").Click());

        // Assert
        Assert.Equal((message.Id, "call-7", false), received);
    }

    #endregion
}
