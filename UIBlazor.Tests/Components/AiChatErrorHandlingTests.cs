namespace UIBlazor.Tests.Components;

/// <summary>
/// Failure handling of <see cref="AiChat"/>: which failures spend the retry budget, what the user
/// sees, and what the failed attempt leaves behind. The classification itself lives in
/// <see cref="LlmApiException.IsRetryable"/> (see <c>docs/dev/llm-transport.md</c>).
/// </summary>
public class AiChatErrorHandlingTests : AiChatTestsBase
{
    /// <summary>
    /// A retryable failure does not overwrite the partial content of the failed attempt: the
    /// message is removed before the retry, so the overwrite would be dead work AND would erase
    /// what the model already produced.
    /// </summary>
    [Fact]
    public async Task RetryableError_PartialContentNotOverwritten()
    {
        // Arrange - stream fails after yielding partial content; MaxRetries = 2 → one retry,
        // the second attempt also fails → final error.
        CommonOptions.MaxRetries = 2;

        // Content snapshots AFTER the pump returns from the stream (including any
        // HandleErrorAsync overwrite): the old code (overwrite before retry) would leave
        // "Error: boom [1/2]" in the snapshot — the regression is caught by name.
        var postStreamContentSnapshots = new List<string>();
        SetupFailingStream(postStreamContentSnapshots, failureMessage: "boom");

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert
        // The FIRST attempt's message kept the partial content (NOT overwritten with the
        // error before the retry — the overwrite+segments-rebuild was dead work).
        Assert.Equal("Partial answer before failure", postStreamContentSnapshots[0]);

        // Retry budget exhausted: the final message DOES show the error.
        var messages = Session.GetMessagesSnapshot();
        // user + the technical note of the retried attempt + assistant with error.
        Assert.Equal(3, messages.Count);

        // The retried attempt did not vanish silently: a technical note records it in place of
        // the removed turn. It is a trace, never context — BuildWire must not send it.
        var note = Assert.Single(messages, m => m.IsTechnical);
        Assert.Contains(string.Format(SharedResource.RetryNoteFormat, 1, 2, 2), note.Content);
        Assert.Contains("boom", note.Content);
        var wireContents = Session.BuildWire("sys").Messages.Select(m => m.Content ?? string.Empty).ToList();
        Assert.DoesNotContain(note.Content, wireContents);

        Assert.Contains("Error:", messages[2].Content);
        Assert.Contains("boom", messages[2].Content);
    }

    /// <summary>
    /// A permanent failure is reported on the FIRST attempt. Before the classification existed
    /// the user waited through the whole budget (~19 minutes with the defaults) to be told about a
    /// wrong API key — an answer that never changed and was knowable immediately.
    /// </summary>
    [Fact]
    public async Task PermanentFailure_NotRetried_ReportedImmediately()
    {
        // Arrange: plenty of budget, but the failure cannot succeed on a repeat.
        CommonOptions.MaxRetries = 10;
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupTurnScripts((_, ct) => ThrowLlmAsync(
            LlmApiException.ForHttpStatus(401, "HttpCode: 401 | invalid api key"), ct));
        // The pump is what enumerates the scripted stream — without it the failure never surfaces.
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert: exactly ONE turn was requested.
        MockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // No retry badge was armed, and no countdown was shown for an attempt that never happened.
        var messages = Session.GetMessagesSnapshot();
        Assert.Equal(2, messages.Count); // user + error, no technical note
        Assert.DoesNotContain(messages, m => m.IsTechnical);

        // The counter is omitted: "[1/10]" would claim nine attempts that will not happen.
        Assert.Contains("Error:", messages[1].Content);
        Assert.Contains("401", messages[1].Content);
        Assert.DoesNotContain("/10]", messages[1].Content);
    }

    /// <summary>
    /// An exception that is not an <see cref="LlmApiException"/> means a bug in our own code (the
    /// transport wraps everything it produces). Retrying it hides the bug behind minutes of
    /// waiting; it must surface at once with its original type preserved in the log.
    /// </summary>
    [Fact]
    public async Task NonLlmException_NotRetried_ReportedImmediately()
    {
        CommonOptions.MaxRetries = 10;
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupTurnScripts((_, ct) => ThrowLlmAsync(new InvalidOperationException("bug in our code"), ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        MockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Once);

        var messages = Session.GetMessagesSnapshot();
        Assert.Equal(2, messages.Count);
        Assert.Contains("bug in our code", messages[1].Content);
        Assert.DoesNotContain(messages, m => m.IsTechnical);
    }

    /// <summary>
    /// A retryable failure still spends the budget exactly as before: the classification only
    /// narrows what is retried, it never widens it.
    /// </summary>
    [Fact]
    public async Task RetryableFailure_StillConsumesBudget()
    {
        CommonOptions.MaxRetries = 2;
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        // No status: a timeout / transport failure — the cause is unknown, so the retry is kept.
        SetupTurnScripts((_, ct) => ThrowLlmAsync(new LlmApiException("Request timed out after 100s"), ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Two attempts (budget of 2) and one technical note left where the first turn was dropped.
        MockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));

        var messages = Session.GetMessagesSnapshot();
        Assert.Single(messages, m => m.IsTechnical);
        Assert.Contains("Error:", messages[^1].Content);
        Assert.Contains("[2/2]", messages[^1].Content);
    }

    [Fact]
    public async Task ExhaustedRetries_FinalMessageShowsError()
    {
        // Arrange - stream always fails; MaxRetries = 1 → no retries, error written to the message
        CommonOptions.MaxRetries = 1;

        SetupFailingStream(postStreamContentSnapshots: null, failureMessage: "boom");

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - retry budget exhausted: the error IS written to the final message
        var messages = Session.GetMessagesSnapshot();
        Assert.Equal(2, messages.Count); // user + assistant with error
        Assert.Contains("Error:", messages[1].Content);
        Assert.Contains("boom", messages[1].Content);
    }

    /// <summary>
    /// A retryable failure of the LAST turn must not wipe the COMPLETED turns: only the partial
    /// turn is removed before the retry (it is never re-sent), so the retried turn sees a
    /// consistent wire history and the finished work stays in the session.
    /// </summary>
    [Fact]
    public async Task RetryableError_MultiTurn_CompletedTurnsKept_RetryContinues()
    {
        // Arrange - MaxRetries = 2 → exactly ONE retry of the failing turn.
        CommonOptions.MaxRetries = 2;
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        SetupTurnScripts(
            // Turn 1: text + a tool call → the turn completes, the loop continues.
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1 partial", ToolCalls = [toolCall] }], ct),
            // Turn 2 and every retry of it: partial content, then the transport dies.
            (_, ct) => StreamThenThrowAsync([new ChatDelta { Content = "turn2 partial" }], "boom", ct));

        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - turn 1 (completed) survives; the failed turn 2 was dropped before the retry
        // and replaced by the retry's message carrying the final error.
        MockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3)); // turn 1 + turn 2 + one retry

        var messages = Session.GetMessagesSnapshot();
        // user + turn1 (kept) + technical note of the retried turn2 + turn2 (error).
        Assert.Equal(4, messages.Count);
        Assert.Single(messages, m => m.Content == "turn1 partial");
        Assert.DoesNotContain("turn2 partial", messages.Select(m => m.Content));

        // The retried turn left a note where it was dropped — after the completed turn 1.
        var note = Assert.Single(messages, m => m.IsTechnical);
        Assert.Contains(string.Format(SharedResource.RetryNoteFormat, 1, 2, 2), note.Content);
        Assert.Equal(2, messages.IndexOf(note));

        Assert.Contains("Error:", messages[3].Content);
        Assert.Contains("boom", messages[3].Content);
        Assert.All(messages, m => Assert.False(m.IsStreaming));
    }
}
