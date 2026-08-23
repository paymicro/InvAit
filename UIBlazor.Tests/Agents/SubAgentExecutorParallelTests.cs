namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_TwoSubAgents_ExecutedInParallel_BothCompleteSuccessfully()
    {
        // Arrange — two sub-agent executions running in parallel via Task.WhenAll.
        // Each should get its own SubAgentMessage, session, and complete independently.
        var toolCall1 = new ToolCall();
        var toolCall2 = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Track which sub-agent session is being processed so we can return distinct content.
        // The ChatService mock is shared, but each call receives a distinct ConversationSession.
        var sessionContentMap = new ConcurrentDictionary<string, string>();

        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (session, _, _, resultCapture, _) =>
                {
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = null;
                })
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    // Generate a unique result based on the message's hash to distinguish parallel executions
                    var content = $"Result-{msg.GetHashCode() % 100}";
                    msg.Content = content;
                    onContent?.Invoke(content);
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act — run both sub-agents in parallel
        var task1 = _executor.ExecuteAsync(args, toolCall1, CancellationToken.None);
        var task2 = _executor.ExecuteAsync(args, toolCall2, CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        // Assert — both sub-agents completed successfully
        Assert.True(results[0].Success);
        Assert.True(results[1].Success);

        // Each tool call has its own distinct SubAgentMessage
        Assert.NotNull(toolCall1.SubAgent);
        Assert.NotNull(toolCall2.SubAgent);
        Assert.NotSame(toolCall1.SubAgent, toolCall2.SubAgent);

        // Both have Completed status
        Assert.Equal(SubAgentStatus.Completed, toolCall1.SubAgent!.Status);
        Assert.Equal(SubAgentStatus.Completed, toolCall2.SubAgent!.Status);

        // Both have unique IDs
        Assert.NotEqual(toolCall1.SubAgent!.Id, toolCall2.SubAgent!.Id);

        // Both have messages (at least user task + assistant response)
        Assert.NotEmpty(toolCall1.SubAgent!.GetMessages());
        Assert.NotEmpty(toolCall2.SubAgent!.GetMessages());
    }

    [Fact]
    public async Task ExecuteAsync_TwoSubAgents_ExecutedInParallel_HaveSeparateSessions()
    {
        // Arrange — verify that parallel sub-agent executions create separate sessions
        // with unique IDs, ensuring no shared state between them.
        var toolCall1 = new ToolCall();
        var toolCall2 = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        var capturedSessions = new ConcurrentBag<ConversationSession>();

        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (session, _, _, resultCapture, _) =>
                {
                    capturedSessions.Add(session);
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = null;
                })
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    msg.Content = "Done";
                    onContent?.Invoke("Done");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act — run both in parallel
        await Task.WhenAll(
            _executor.ExecuteAsync(args, toolCall1, CancellationToken.None),
            _executor.ExecuteAsync(args, toolCall2, CancellationToken.None));

        // Assert — two distinct sessions were created
        Assert.Equal(2, capturedSessions.Count);
        var sessionList = capturedSessions.ToList();
        Assert.NotEqual(sessionList[0].Id, sessionList[1].Id);
        Assert.StartsWith("subagent_", sessionList[0].Id);
        Assert.StartsWith("subagent_", sessionList[1].Id);
    }

    [Fact]
    public async Task ExecuteAsync_TwoSubAgents_ExecutedInParallel_OneCancelledOneCompletes()
    {
        // Arrange — one sub-agent is cancelled while the other completes normally.
        // This verifies that cancellation of one does not affect the other.
        var toolCall1 = new ToolCall();
        var toolCall2 = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        using var cts1 = new CancellationTokenSource();
        cts1.Cancel(); // Pre-cancel the first sub-agent's token

        // Set up two different behaviors based on the cancellation token
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, ct) =>
                {
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);

                    msg.Content = "Completed successfully";
                    onContent?.Invoke("Completed successfully");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act — run both in parallel: one with cancelled token, one with normal token
        var task1 = _executor.ExecuteAsync(args, toolCall1, cts1.Token);
        var task2 = _executor.ExecuteAsync(args, toolCall2, CancellationToken.None);
        var results = await Task.WhenAll(task1, task2);

        // Assert — first sub-agent was cancelled, second completed successfully
        Assert.False(results[0].Success);
        Assert.True(results[1].Success);

        Assert.Equal(SubAgentStatus.Cancelled, toolCall1.SubAgent!.Status);
        Assert.Equal(SubAgentStatus.Completed, toolCall2.SubAgent!.Status);

        // They are distinct sub-agent instances
        Assert.NotSame(toolCall1.SubAgent, toolCall2.SubAgent);
    }

    [Fact]
    public async Task ExecuteAsync_TwoSubAgents_ExecutedInParallel_BothHaveDistinctResults()
    {
        // Arrange — verify that parallel sub-agents produce distinct, non-overlapping results.
        var toolCall1 = new ToolCall();
        var toolCall2 = new ToolCall();
        var args1 = JsonSerializer.Serialize(new { task = "Task A", systemPrompt = "Prompt A" });
        var args2 = JsonSerializer.Serialize(new { task = "Task B", systemPrompt = "Prompt B" });

        // Use a counter to assign distinct content to each parallel execution
        var executionCounter = 0;

        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (_, _, _, resultCapture, _) =>
                {
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = null;
                })
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    var num = Interlocked.Increment(ref executionCounter);
                    var content = $"Result for execution #{num}";
                    msg.Content = content;
                    onContent?.Invoke(content);
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act — run both in parallel
        var results = await Task.WhenAll(
            _executor.ExecuteAsync(args1, toolCall1, CancellationToken.None),
            _executor.ExecuteAsync(args2, toolCall2, CancellationToken.None));

        // Assert — both results are distinct
        Assert.True(results[0].Success);
        Assert.True(results[1].Success);
        Assert.NotEqual(results[0].Result, results[1].Result);

        // Both sub-agents have their own task description
        Assert.Equal("Task A", toolCall1.SubAgent!.Task);
        Assert.Equal("Task B", toolCall2.SubAgent!.Task);

        // Both sub-agents have distinct messages
        var messages1 = toolCall1.SubAgent!.GetMessages();
        var messages2 = toolCall2.SubAgent!.GetMessages();
        Assert.NotEmpty(messages1);
        Assert.NotEmpty(messages2);

        // The first user message in each sub-agent should match its task
        Assert.Equal("Task A", messages1[0].Content);
        Assert.Equal("Task B", messages2[0].Content);
    }
}
