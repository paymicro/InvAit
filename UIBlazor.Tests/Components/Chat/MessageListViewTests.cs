using UIBlazor.Components.Chat;

namespace UIBlazor.Tests.Components.Chat;

/// <summary>
/// Tests for <see cref="MessageListView"/> - the rendered transcript of the MAIN session.
/// <para>
/// <see cref="ChatMessageView"/> is rendered for real (only the leaf markdown/tool renderers are
/// stubbed, the same way <c>ChatMessageViewTests</c> does), because the whole point of the window is
/// WHAT ends up in the DOM: the bubble count, the single plaque, and the fact that the last bubble
/// is still identified as last while the middle of the history is folded away.
/// </para>
/// </summary>
public class MessageListViewTests : BunitContext
{
    private readonly Mock<IToolManager> _mockToolManager = new();

    public MessageListViewTests()
    {
        var mockCommonSettings = new Mock<ICommonSettingsProvider>();
        mockCommonSettings.Setup(x => x.Current).Returns(new CommonOptions { ShowMessageTimings = false });

        Services.AddSingleton(mockCommonSettings.Object);
        Services.AddSingleton(_mockToolManager.Object);
        Services.AddRadzenComponents();

        // Scroll anchoring (captureChatScrollHeight / restoreChatScrollHeight) is cosmetic and
        // absent in a test DOM; loose interop is the repo pattern for such calls.
        JSInterop.Mode = JSRuntimeMode.Loose;

        ComponentFactories.AddStub<MessageContent>(parameters =>
        {
            var message = parameters.Get(p => p.Message);
            return builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "message-content-stub");
                builder.AddContent(2, message?.Content ?? "No content");
                builder.CloseElement();
            };
        });

        ComponentFactories.AddStub<MarkdownBlock>();
    }

    #region Helpers

    private static List<VisualChatMessage> BuildHistory(int count, string content = "m")
    {
        var messages = new List<VisualChatMessage>(count);

        for (var i = 0; i < count; i++)
        {
            messages.Add(new VisualChatMessage
            {
                Role = i % 2 == 0 ? ChatMessageRole.User : ChatMessageRole.Assistant,
                Content = content,
                Timestamp = new DateTime(2024, 1, 15, 10, 30, 0)
            });
        }

        return messages;
    }

    private static ToolCall CreateToolCall(string name = "read_file")
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            Function = new ToolCallFunction { Name = name, Arguments = string.Empty },
            ApprovalStatus = ToolApprovalStatus.Approved,
            Result = null
        };

    private static IReadOnlyList<string> RenderedMessageIds(IRenderedComponent<MessageListView> cut)
        => [.. cut.FindAll(".rz-chat-message").Select(e => e.GetAttribute("data-message-id") ?? string.Empty)];

    #endregion

    #region Window shape

    [Fact]
    public void ShouldRenderOnlyHeadAndTail_ForLongHistory()
    {
        // Arrange
        var messages = BuildHistory(300);

        // Act
        var cut = Render<MessageListView>(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-a"));

        // Assert - the fold costs exactly the head plus the tail, and one plaque states the rest.
        var rendered = RenderedMessageIds(cut);
        Assert.Equal(MessageWindowPolicy.HeadWindowCount + MessageWindowPolicy.TailWindowCount, rendered.Count);
        Assert.Single(cut.FindAll(".hidden-messages-plaque"));

        Assert.Equal(messages.Take(MessageWindowPolicy.HeadWindowCount).Select(m => m.Id), rendered.Take(MessageWindowPolicy.HeadWindowCount));
        Assert.Equal(messages.TakeLast(MessageWindowPolicy.TailWindowCount).Select(m => m.Id), rendered.TakeLast(MessageWindowPolicy.TailWindowCount));

        // A message from the folded middle is genuinely absent from the DOM, not merely hidden by CSS.
        Assert.DoesNotContain(messages[150].Id, rendered);
    }

    [Fact]
    public void ShouldRenderEveryMessage_ForShortHistory()
    {
        // Arrange - below the fold threshold the transcript is complete.
        var messages = BuildHistory(30);

        // Act
        var cut = Render<MessageListView>(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-a"));

        // Assert
        Assert.Equal(30, cut.FindAll(".rz-chat-message").Count);
        Assert.Empty(cut.FindAll(".hidden-messages-plaque"));
    }

    #endregion

    #region Plaque

    [Fact]
    public void ShouldShowFormattedStatistics_OnPlaque()
    {
        // Arrange - 260 hidden messages of one character each and 42 tool calls in the middle:
        // 260 chars => ~65 tokens, and every number stays distinguishable in the summary line.
        var messages = BuildHistory(300);
        messages[20].ToolCalls = [.. Enumerable.Range(0, 42).Select(_ => CreateToolCall())];

        // Act
        var cut = Render<MessageListView>(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-a"));

        // Assert
        var plaque = cut.Find(".hidden-messages-plaque");
        Assert.Equal("260", plaque.GetAttribute("data-hidden-count"));

        var stats = cut.Find(".hidden-messages-plaque .divider-row-text").TextContent;
        Assert.Contains("260", stats);
        Assert.Contains("42", stats);
        Assert.Contains("65", stats);
    }

    [Fact]
    public async Task ShouldRevealOnePage_WhenShowMoreClicked()
    {
        // Arrange
        var messages = BuildHistory(300);
        var cut = Render<MessageListView>(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-a"));

        var before = cut.FindAll(".rz-chat-message").Count;

        // Act - the first action of the plaque is "show earlier".
        var showMore = cut.FindAll(".hidden-messages-plaque .divider-row-actions button")[0];
        await cut.InvokeAsync(() => showMore.Click());

        // Assert - exactly one page of the middle became real bubbles.
        cut.WaitForAssertion(() =>
            Assert.Equal(before + MessageWindowPolicy.GroupPageSize, cut.FindAll(".rz-chat-message").Count));

        Assert.Single(cut.FindAll(".hidden-messages-plaque"));
        Assert.Contains(messages[270 - MessageWindowPolicy.GroupPageSize].Id, RenderedMessageIds(cut));
    }

    #endregion

    #region Last message / IsLast

    [Fact]
    public void ShouldRenderLastMessage_AsLastRow()
    {
        // Arrange
        var messages = BuildHistory(300);
        var lastMessage = messages[^1];

        // Act
        var cut = Render<MessageListView>(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-a"));

        // Assert - positional decisions use real indices, so IsLast still belongs to the last message.
        var bubbles = cut.FindAll(".rz-chat-message");
        Assert.Equal(lastMessage.Id, bubbles[^1].GetAttribute("data-message-id"));

        // Regenerate is offered only by the bubble that was told IsLast, and exactly one bubble is.
        var regenerateButtons = cut.FindAll("button").Where(b => b.InnerHtml.Contains("refresh")).ToList();
        Assert.Single(regenerateButtons);
        Assert.Contains("refresh", bubbles[^1].OuterHtml);
    }

    #endregion

    #region EnsureVisibleAsync

    [Fact]
    public async Task EnsureVisibleAsync_ShouldMaterialiseHiddenMessage()
    {
        // Arrange
        var messages = BuildHistory(300);
        var target = messages[150];
        var cut = Render<MessageListView>(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-a"));

        Assert.DoesNotContain(target.Id, RenderedMessageIds(cut));

        // Act
        var result = await cut.InvokeAsync(() => cut.Instance.EnsureVisibleAsync(target.Id));

        // Assert - scrollToMessage can only work on a message that exists in the DOM.
        Assert.True(result);
        Assert.Contains(target.Id, RenderedMessageIds(cut));
    }

    [Fact]
    public async Task EnsureVisibleAsync_ShouldReturnFalse_ForUnknownId()
    {
        // Arrange
        var messages = BuildHistory(300);
        var cut = Render<MessageListView>(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-a"));

        var before = cut.FindAll(".rz-chat-message").Count;

        // Act
        var result = await cut.InvokeAsync(() => cut.Instance.EnsureVisibleAsync("no-such-message"));

        // Assert - an unknown id must not expand the window either.
        Assert.False(result);
        Assert.Equal(before, cut.FindAll(".rz-chat-message").Count);
    }

    [Fact]
    public async Task EnsureVisibleAsync_ShouldReturnTrue_ForAlreadyVisibleMessage()
    {
        // Arrange - the tail is already materialised: no expansion must happen.
        var messages = BuildHistory(300);
        var cut = Render<MessageListView>(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-a"));

        var before = cut.FindAll(".rz-chat-message").Count;

        // Act
        var result = await cut.InvokeAsync(() => cut.Instance.EnsureVisibleAsync(messages[^1].Id));

        // Assert
        Assert.True(result);
        Assert.Equal(before, cut.FindAll(".rz-chat-message").Count);
    }

    #endregion

    #region Session switch

    [Fact]
    public async Task ShouldCollapseWindow_WhenSessionChanges()
    {
        // Arrange
        var messages = BuildHistory(300);
        var cut = Render<MessageListView>(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-a"));

        var showMore = cut.FindAll(".hidden-messages-plaque .divider-row-actions button")[0];
        await cut.InvokeAsync(() => showMore.Click());
        cut.WaitForAssertion(() =>
            Assert.Equal(MessageWindowPolicy.HeadWindowCount + MessageWindowPolicy.TailWindowCount + MessageWindowPolicy.GroupPageSize,
                cut.FindAll(".rz-chat-message").Count));

        // Act - a different session must not inherit the previous session's revealed pages.
        cut.Render(parameters => parameters
            .Add(p => p.Messages, messages)
            .Add(p => p.SessionId, "session-b"));

        // Assert
        Assert.Equal(MessageWindowPolicy.HeadWindowCount + MessageWindowPolicy.TailWindowCount,
            cut.FindAll(".rz-chat-message").Count);
        Assert.Single(cut.FindAll(".hidden-messages-plaque"));
    }

    #endregion
}
