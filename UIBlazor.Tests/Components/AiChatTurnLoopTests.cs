namespace UIBlazor.Tests.Components;

/// <summary>
/// The agent turn loop of <see cref="AiChat"/>: one LLM call per turn, the between-turn mutations
/// reaching the next request, cancellation at every stage, and the tool batch.
/// <para>
/// The invariant behind every test here is that the request is rebuilt from the session at the
/// start of each turn (see <c>docs/dev/agent-turn-loop.md</c>), so an edit, a deletion, a
/// compression, an interject or a mode switch applied between turns is visible to the model.
/// </para>
/// </summary>
public class AiChatTurnLoopTests : AiChatTestsBase
{
    [Fact]
    public async Task TurnLoop_CompressionBetweenTurns_NextTurnRequestIsCompacted()
    {
        // The headline defect (todo.md line 3): the UI showed a compressed session while the
        // RUN kept sending the uncompressed history. The wire history is now rebuilt from the
        // session at the start of every turn, so compaction between turns must reach the model.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        // The service owns the accounting: the threshold is "crossed" once turn 1 exists.
        MockChatService
            .Setup(x => x.NeedsCompression(It.IsAny<ConversationSession>()))
            .Returns((ConversationSession session) =>
                session.GetMessagesSnapshot().Any(m => m.Content == "turn1"));

        // Real compaction as the service performs it: the history is rewritten IN PLACE.
        MockChatService
            .Setup(x => x.CompressSessionAsync(It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession session, CompletionsResult _, CancellationToken _) =>
            {
                var toCollapse = session.GetMessagesSnapshot()
                    .Where(m => m.Role != ChatMessageRole.System)
                    .ToList();
                session.CollapseMessages(toCollapse, new VisualChatMessage
                {
                    Role = ChatMessageRole.Assistant,
                    Content = "SUMMARY"
                });
                return AsyncEnumerable.Empty<ChatDelta>();
            });

        var toolCall = CreateToolCall("call_1", "read_file");
        List<VisualChatMessage>? historyAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                historyAtTurn2 = [.. Session.GetMessagesSnapshot()];
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The compaction ran exactly once, BETWEEN the turns.
        MockChatService.Verify(x => x.CompressSessionAsync(It.IsAny<ConversationSession>(),
            It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()), Times.Once);

        // Turn 2 was requested from the COMPACTED history: the pre-compression turn is gone,
        // the summary travels instead.
        Assert.NotNull(historyAtTurn2);
        Assert.Contains(historyAtTurn2!, m => m.Content == "SUMMARY");
        Assert.DoesNotContain(historyAtTurn2!, m => m.Content == "turn1");
    }

    [Fact]
    public async Task TurnLoop_MessageEditedBetweenTurns_NextTurnSeesEditedContent()
    {
        // An edit applied between the turns (there was NO compensation for this before: the
        // wire history was frozen for the whole run).
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");

        MockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> _, AppMode _, CancellationToken _) =>
            {
                // Between the turns: the user edits the original request.
                var userMessage = Session.GetMessagesSnapshot().First(m => m.Role == ChatMessageRole.User);
                Session.UpdateMessage(userMessage.Id, "edited request");
                return Task.CompletedTask;
            });

        List<VisualChatMessage>? historyAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                historyAtTurn2 = [.. Session.GetMessagesSnapshot()];
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The second turn saw the edited text, not the original one.
        Assert.NotNull(historyAtTurn2);
        Assert.Contains(historyAtTurn2!, m => m.Content == "edited request");
        Assert.DoesNotContain(historyAtTurn2!, m => m.Content == "hi");
    }

    [Fact]
    public async Task TurnLoop_MessageDeletedBetweenTurns_NextTurnOmitsIt()
    {
        // A deletion applied between the turns (also without compensation before the refactor).
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");

        MockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> _, AppMode _, CancellationToken _) =>
            {
                var userMessage = Session.GetMessagesSnapshot().First(m => m.Role == ChatMessageRole.User);
                Session.RemoveMessage(userMessage.Id);
                return Task.CompletedTask;
            });

        List<VisualChatMessage>? historyAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                historyAtTurn2 = [.. Session.GetMessagesSnapshot()];
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The deleted message is gone from the second request.
        Assert.NotNull(historyAtTurn2);
        Assert.DoesNotContain(historyAtTurn2!, m => m.Content == "hi");
    }

    [Fact]
    public async Task TurnLoop_ModeSwitchedBetweenTurns_SecondTurnUsesNewMode()
    {
        // switch_mode must affect the NEXT turn: the system prompt and the tool set are derived
        // from the session mode when the request is built.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", BasicEnum.SwitchMode);

        MockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> _, AppMode _, CancellationToken _) =>
            {
                Session.Mode = AppMode.Plan;
                return Task.CompletedTask;
            });

        AppMode? modeAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                modeAtTurn2 = Session.Mode;
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The second turn ran under the NEW mode.
        Assert.Equal(AppMode.Plan, modeAtTurn2);
        MockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task TurnLoop_InterjectBetweenTurns_ReachesModel()
    {
        // A message written BETWEEN the turns (while the tool batch runs) must be answered:
        // afterwards the run continues with another turn that carries it.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        MockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> _, AppMode _, CancellationToken _) =>
            {
                Session.AddMessage(VisualChatMessage.CreateUser("also check the tests"));
                return Task.CompletedTask;
            });

        List<VisualChatMessage>? historyAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                historyAtTurn2 = [.. Session.GetMessagesSnapshot()];
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The interject reaches the model in the second turn.
        Assert.NotNull(historyAtTurn2);
        Assert.Contains(historyAtTurn2!, m => m.Content == "also check the tests");
        MockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task TurnLoop_TwoTurns_ExactlyTwoCompletionsCalls_AndTwoAssistantMessages()
    {
        // Arrange - turn 1 asks for a tool (that is what continues the run), turn 2 answers.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        SetupTurnStreams(
            [new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }],
            [new ChatDelta { Content = "turn2" }]);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - exactly two LLM turns, no empty placeholder anywhere.
        MockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));

        var messages = Session.GetMessagesSnapshot();
        Assert.Equal(3, messages.Count); // user + two assistant turns
        Assert.Equal(2, messages.Count(m => m.Role == ChatMessageRole.Assistant));
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Assistant
                                             && string.IsNullOrEmpty(m.Content)
                                             && m.ToolCalls is not { Count: > 0 });
        Assert.Equal("turn1", messages[1].Content);
        Assert.Equal("turn2", messages[2].Content);
        Assert.All(messages, m => Assert.False(m.IsStreaming));
    }

    [Fact]
    public async Task TurnLoop_TurnWithoutPayload_LeavesNoMessage()
    {
        // Arrange - the turn produces nothing at all (no content, no reasoning, no tool calls)
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupTurnStreams([]);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - no payload, so the eagerly added bubble was removed again:
        // an empty turn leaves NO message behind.
        var messages = Session.GetMessagesSnapshot();
        Assert.Single(messages);
        Assert.Equal(ChatMessageRole.User, messages[0].Role);
    }

    [Fact(Timeout = 20000)]
    public async Task TurnLoop_BeforeFirstToken_ShowsPlaceholderAndCancelButton()
    {
        // The dead window: the request was accepted but the provider has not sent a single byte.
        // The UI looked frozen there - no bubble, no streaming indicator and no way to cancel -
        // because the turn message was only added on the first payload AND IsLoading was set
        // without a re-render.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupTurnScripts((_, ct) => BlockingStreamAsync(gate.Task, ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Wait for the run to reach the transport: nothing has been delivered at this point.
        await WaitUntilAsync(() => Session.GetMessagesSnapshot()
            .Any(m => m.Role == ChatMessageRole.Assistant));

        // The bubble exists, is empty and open for streaming.
        var placeholder = Assert.Single(Session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.Assistant);
        Assert.True(placeholder.IsStreaming);
        Assert.Equal(string.Empty, placeholder.Content);

        // It is on screen (the view itself is stubbed in bUnit: the session entry is rendered)...
        Assert.NotEmpty(cut.FindAll($"[data-message-id='{placeholder.Id}']"));

        // ...and the parent already painted the busy state, so the Cancel button (which lives in
        // the input child) is available while nothing has arrived.
        Assert.Equal("true", cut.Find(".ai-chat-input-stub").GetAttribute("data-loading"));

        gate.TrySetResult();
        await run;

        // A run that ends without payload still leaves no bubble behind.
        Assert.DoesNotContain(Session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.Assistant);
    }

    [Fact]
    public async Task TurnLoop_CancelAfterFirstTurn_NoSecondCall_AllMessagesClosed()
    {
        // Arrange - turn 1 asks for a tool; the cancellation arrives while the batch runs.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        SetupTurnStreams(
            [new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }],
            [new ChatDelta { Content = "turn2" }]);
        SetupProcessStreamConsume();

        AiChat? instance = null;
        MockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns(async () => await CancelRunAsync(instance!));

        var cut = Render<AiChat>();
        instance = cut.Instance;

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - ONE token source per run: the cancelled run never starts another turn and
        // every message it created is closed (otherwise it would be filtered out of the wire).
        MockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.All(Session.GetMessagesSnapshot(), m => Assert.False(m.IsStreaming));
    }

    [Fact(Timeout = 20000)]
    public async Task TurnLoop_CancelBeforeFirstToken_LeavesNoBubble()
    {
        // The turn message is added EAGERLY now (the bubble is the feedback that the run started).
        // Cancelling inside that window must not leave it behind: nothing arrived, so there is
        // nothing to show and nothing to send. Regression guard - the cancel handler writes
        // "Cancelled by user..." into an empty message, so a payload check placed AFTER it always
        // said "non-empty" and the bubble survived.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupTurnScripts((_, ct) => BlockingStreamAsync(gate.Task, ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The run has reached the transport and nothing has been delivered yet.
        await WaitUntilAsync(() => Session.GetMessagesSnapshot()
            .Any(m => m.Role == ChatMessageRole.Assistant));

        // Act - the Cancel button path.
        await cut.InvokeAsync(async () => await CancelRunAsync(cut.Instance));
        gate.TrySetResult();
        await run;

        // Assert - only the user message survives, and it is not "Cancelled by user...".
        var messages = Session.GetMessagesSnapshot();
        Assert.Single(messages);
        Assert.Equal(ChatMessageRole.User, messages[0].Role);
        Assert.DoesNotContain("Cancelled by user", string.Join('|', messages.Select(m => m.Content)));
    }

    [Fact(Timeout = 20000)]
    public async Task TurnLoop_CancelAfterPartialContent_KeepsPartialAnswer()
    {
        // The counterpart: once the model produced text, cancelling keeps it. The user asked to
        // stop, not to discard what was already written - and the handler must not overwrite it.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupTurnScripts((_, ct) => PartialThenBlockAsync(gate.Task, ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        await WaitUntilAsync(() => Session.GetMessagesSnapshot()
            .Any(m => m.Content == "partial"));

        // Act
        await cut.InvokeAsync(async () => await CancelRunAsync(cut.Instance));
        gate.TrySetResult();
        await run;

        // Assert - the partial answer is intact and was not replaced by the cancel notice.
        var turn = Assert.Single(Session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.Assistant);
        Assert.Equal("partial", turn.Content);
        Assert.False(turn.IsStreaming);
    }

    [Fact(Timeout = 20000)]
    public async Task TurnLoop_CancelBeforeTokenWithOpenToolCalls_KeepsCallsAndFillsResults()
    {
        // Tool calls are payload: the wire history needs assistant(tool_calls) followed by a tool
        // message for EVERY call, so the bubble must stay and its results must be stubbed.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupTurnScripts((_, ct) => ToolCallThenBlockAsync(toolCall, gate.Task, ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        await WaitUntilAsync(() => Session.GetMessagesSnapshot()
            .Any(m => m.ToolCalls is { Count: > 0 }));

        // Act
        await cut.InvokeAsync(async () => await CancelRunAsync(cut.Instance));
        gate.TrySetResult();
        await run;

        // Assert
        var turn = Assert.Single(Session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.Assistant);
        var call = Assert.Single(turn.ToolCalls!);
        Assert.NotNull(call.Result);
        Assert.False(call.Result!.Success);
    }

    [Fact]
    public async Task TurnLoop_ToolBatch_PreparesApprovalsAndExecutesExactlyOnce()
    {
        // Arrange - PrepareToolsForApprovals cancels every pending gate first, so a second call
        // per batch would drop an approval the user already made.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        SetupTurnStreams(
            [new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }],
            [new ChatDelta { Content = "turn2" }]);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert
        MockToolCallHandler.Verify(x => x.PrepareToolsForApprovals(It.IsAny<List<ToolCall>>()), Times.Once);
        MockToolCallHandler.Verify(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(),
            It.IsAny<AppMode>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TurnLoop_CancelledMidBatch_EveryToolCallHasResult()
    {
        // Arrange - two calls in the batch; the run is cancelled after the first one finished.
        // An assistant(tool_calls) turn without matching tool messages is a hard API 400 on the
        // next turn, so the remainder must be stubbed.
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var first = CreateToolCall("call_1", "read_file");
        var second = CreateToolCall("call_2", "read_file");
        SetupTurnStreams(
            [new ChatDelta { Content = "turn1", ToolCalls = [first, second] }],
            [new ChatDelta { Content = "turn2" }]);
        SetupProcessStreamConsume();

        AiChat? instance = null;
        MockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns(async (List<ToolCall> calls, AppMode _, CancellationToken ct) =>
            {
                calls[0].Result = new ToolResult { Name = calls[0].Function.Name, Content = "done", Success = true };
                await CancelRunAsync(instance!);
                throw new OperationCanceledException(ct);
            });

        var cut = Render<AiChat>();
        instance = cut.Instance;

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert
        var turn = Session.GetMessagesSnapshot()[1];
        Assert.NotNull(turn.ToolCalls);
        Assert.Equal(2, turn.ToolCalls.Count);
        Assert.All(turn.ToolCalls, call => Assert.NotNull(call.Result));
        Assert.True(turn.ToolCalls[0].Result!.Success);
        Assert.False(turn.ToolCalls[1].Result!.Success);
        Assert.All(Session.GetMessagesSnapshot(), m => Assert.False(m.IsStreaming));
    }
}
