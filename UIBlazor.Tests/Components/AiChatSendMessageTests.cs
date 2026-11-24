namespace UIBlazor.Tests.Components;

/// <summary>
/// Sending a message: the interject window, the compression gate, the command dispatcher and the
/// cancel-then-send regression. The turn loop itself is covered by <c>AiChatTurnLoopTests</c>.
/// </summary>
public class AiChatSendMessageTests : AiChatTestsBase
{
    [Fact]
    public async Task ShouldSendMessage_WhenContentProvided()
    {
        // Arrange - the turn produces no payload at all
        var content = "Test message";
        MockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatDelta>);
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var cut = Render<AiChat>();

        // Act - call SendMessageAsync directly
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync(content));

        // Assert - the user message is appended, and a turn WITHOUT payload leaves no
        // empty assistant bubble behind (lazy turn message).
        Assert.Equal(1, Session.GetMessageCount());
        var sessionMessages = Session.GetMessagesSnapshot();
        Assert.Equal(content, sessionMessages[0].Content);
        Assert.Equal(ChatMessageRole.User, sessionMessages[0].Role);
    }

    [Fact]
    public async Task SendMessageAsync_WhileStreaming_InterjectAddedWithoutNewRun()
    {
        // Arrange: the turn streams until released — IsLoading stays true.
        var release = new TaskCompletionSource();
        MockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession _, VisualChatMessage? _, IReadOnlyList<Tool>? _, CompletionsResult capture, CancellationToken ct)
                => StreamUntilReleased(release, capture, ct));
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Start the run (IsLoading = true, interject window open).
        var firstRun = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("start"));

        // Wait until the first delta is consumed — guarantees the interject window is open
        // (_acceptInterjects is set before ProcessStreamAsync starts consuming).
        await WaitUntilAsync(() => Session.GetMessagesSnapshot()[^1].Content == "streaming...");

        // Act: send a second message while the run is streaming — it must be ADDED to the
        // session right after the streaming message (interject), NOT silently dropped and
        // NOT followed by a new assistant placeholder (no new run).
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("interject"));

        var snapshot = Session.GetMessagesSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(ChatMessageRole.User, snapshot[^1].Role);
        Assert.Equal("interject", snapshot[^1].Content);
        Assert.Equal(1, RunCount); // GetTurnCompletionsAsync called exactly once (no new run)

        // Finish the run.
        release.TrySetResult();
        await firstRun;
    }

    [Fact]
    public async Task SendMessageAsync_DuringCompression_MessageRejected()
    {
        // Arrange: the compress command sets IsLoading = true and runs compression —
        // the interject window is closed (_acceptInterjects is only open for the agent loop).
        // ProcessStreamAsync blocks until released, so IsLoading stays true.
        var release = new TaskCompletionSource();
        MockChatService.Setup(x => x.CompressSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatDelta>);
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        MockChatService.Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(release.Task);

        var cut = Render<AiChat>();
        var compactRun = cut.InvokeAsync(async () => await cut.Instance.HandleCommandAsync(BuiltInCommand.Compress));

        // Wait until the compression assistant message is added (IsLoading = true).
        await WaitUntilAsync(() => Session.GetMessageCount() >= 1);

        // Act: a message sent during compression must be REJECTED — compression rewrites
        // the history from a snapshot and would silently drop the interject.
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("during compression"));

        var snapshot = Session.GetMessagesSnapshot();
        Assert.Single(snapshot);
        Assert.Equal(ChatMessageRole.Assistant, snapshot[^1].Role);
        Assert.False(snapshot[^1].Content == "during compression");

        release.TrySetResult();
        await compactRun;
    }

    /// <summary>
    /// The suggestion menu hands the dispatcher a bare name ("compress"), while a command typed
    /// by hand arrives as "/compress". Both spellings must reach the same case — the slash used to
    /// be part of the literal the dispatcher compared against, so renaming the command or fixing a
    /// typo (the old name was "compact") silently turned one of the two paths into a no-op.
    /// </summary>
    [Theory]
    [InlineData(BuiltInCommand.Compress)]
    [InlineData("/compress")]
    [InlineData("  /compress  ")]
    public async Task HandleCommandAsync_NormalisesBuiltInCommand(string command)
    {
        // Arrange - the compression stream never yields: only the CALL matters here.
        MockChatService
            .Setup(x => x.CompressSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatDelta>);
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.HandleCommandAsync(command));

        // Assert
        MockChatService.Verify(x => x.CompressSessionAsync(It.IsAny<ConversationSession>(),
            It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression for the cached-cancelled-token bug: cancel during a run, then send a
    /// message. The old code used the run's _cts.Token for the UI scroll (ScrollToBottomAsync):
    /// after cancellation the token stayed cancelled (recreated only inside
    /// GetAiResponseAsync, which never ran), so SendMessageAsync threw
    /// OperationCanceledException between AddMessage and GetAiResponseAsync — the message
    /// appeared in the chat but was never sent to the agent and never saved to localStorage.
    /// </summary>
    [Fact]
    public async Task SendMessageAsync_AfterCancel_NewRunStartsAndSessionSaved()
    {
        // Arrange: run 1 streams until released (cancel target).
        var release = new TaskCompletionSource();
        var runCount = 0;
        // Local stream factory: does NOT touch the fixture's run counter that
        // SendMessageAsync_WhileStreaming_InterjectAddedWithoutNewRun asserts on.
        MockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession _, VisualChatMessage? _, IReadOnlyList<Tool>? _, CompletionsResult capture, CancellationToken ct) =>
            {
                Interlocked.Increment(ref runCount);
                capture.Model = "test-model";
                return StreamUntilReleasedLocal(release, capture, ct);
            });
        var saveCount = 0;
        MockChatService.Setup(x => x.SaveSessionAsync())
            .Callback(() => Interlocked.Increment(ref saveCount))
            .Returns(Task.CompletedTask);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var firstRun = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("start"));
        await WaitUntilAsync(() => Session.GetMessagesSnapshot()[^1].Content == "streaming...");

        // Act 1: cancel (the same path as the Cancel button in AiChatInput).
        await cut.InvokeAsync(async () => await CancelRunAsync(cut.Instance));
        await WaitUntilAsync(() => Session.GetMessagesSnapshot().All(m => !m.IsStreaming));

        // Release the gate AFTER run 1 fully unwound: the second run reuses the same
        // stream factory, so an unreleased gate would block the new run forever.
        release.TrySetResult();
        await firstRun;

        // Act 2: a message after cancel must NOT throw on the cancelled scroll token.
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("after cancel"));

        // Assert: a new run started, the message is in the session, the session was saved.
        Assert.Equal(2, runCount);
        Assert.Contains(Session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.User && m.Content == "after cancel");
        Assert.True(saveCount > 0);
    }

    [Fact]
    public async Task ShouldNotSendMessage_WhenContentIsEmpty()
    {
        // Arrange
        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync(""));

        // Assert
        Assert.Empty(Session.GetMessagesSnapshot());
    }

    [Fact]
    public async Task ShouldNotSendMessage_WhenContentIsWhitespace()
    {
        // Arrange
        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("   "));

        // Assert
        Assert.Empty(Session.GetMessagesSnapshot());
    }
}
