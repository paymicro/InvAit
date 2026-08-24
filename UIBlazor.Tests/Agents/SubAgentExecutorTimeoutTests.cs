namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_TimeoutDisabled_WhenZero_SubAgentCompletesNormally()
    {
        // Arrange — MaxExecutionTimePerSubAgent = 0 means the time limit is disabled.
        // The sub-agent should complete without any timeout interference.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 0,
            MaxIterationsPerSubAgent = 20,
            MaxExecutionTimePerSubAgent = 0
        });

        SetupChatServiceToReturnContent("Completed successfully");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Completed successfully", result.Result);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutDisabled_WhenZero_DoesNotAffectLoopIterations()
    {
        // Arrange — with time limit disabled (0), the sub-agent should be able to
        // complete multiple loop iterations without being stopped by a timeout.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 0,
            MaxIterationsPerSubAgent = 20,
            MaxExecutionTimePerSubAgent = 0
        });

        var completionsCallCount = 0;

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
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    // First 3 calls return tool calls, 4th returns final answer
                    resultCapture.AccumulatedToolCalls = completionsCallCount <= 3
                        ? [new ToolCall { Id = $"tc{completionsCallCount}", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
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
                    msg.Content = completionsCallCount == 4 ? "Final answer" : $"Iteration {completionsCallCount}";
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — all 4 iterations completed without timeout interruption
        Assert.True(result.Success);
        Assert.Equal("Final answer", result.Result);
        Assert.Equal(4, completionsCallCount);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutExceeded_ReturnsTimeoutMessage()
    {
        // Arrange — MaxExecutionTimePerSubAgent = 0.1 (6 seconds).
        // ProcessStreamAsync is mocked to actually delay 7 seconds (using Returns with async lambda,
        // because Moq Callback with async lambda is fire-and-forget and won't await the delay).
        // On the second iteration, the timeout check fires and returns a timeout message.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 0,
            MaxIterationsPerSubAgent = 20,
            MaxExecutionTimePerSubAgent = 2
        });

        var completionsCallCount = 0;

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
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    // First call returns tool calls so the loop continues to a second iteration
                    // where the timeout check will fire.
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                })
            .Returns(CreateEmptyDeltaStream());

        // Use Returns with async lambda so the delay is actually awaited.
        // Moq Callback with async lambda is fire-and-forget — the delay would not block.
        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                async (msg, _, onContent, _, _, _, ct) =>
                {
                    msg.Content = "Working on the task";
                    onContent?.Invoke("Working on the task");
                    // Delay 7 seconds to exceed the 6-second timeout
                    await Task.Delay(TimeSpan.FromSeconds(7), ct);
                });

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — the result should contain the execution time limit message
        Assert.True(result.Success);
        Assert.Contains("execution time limit", result.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Working on the task", result.Result);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutExceeded_StatusIsCompleted()
    {
        // Arrange — when the execution time limit is exceeded, the sub-agent completes
        // gracefully (status = Completed, not Failed), similar to token budget and iteration limits.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 0,
            MaxIterationsPerSubAgent = 20,
            MaxExecutionTimePerSubAgent = 2
        });

        var completionsCallCount = 0;

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
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
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
            .Returns<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                async (msg, _, onContent, _, _, _, ct) =>
                {
                    msg.Content = "Processing";
                    onContent?.Invoke("Processing");
                    await Task.Delay(TimeSpan.FromSeconds(7), ct);
                });

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — status should be Completed (graceful stop), not Failed
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutExceeded_IncludesLastResponseInResult()
    {
        // Arrange — when the execution time limit is exceeded, the result should include
        // the last assistant response content so the main agent has context.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 0,
            MaxIterationsPerSubAgent = 20,
            MaxExecutionTimePerSubAgent = 2
        });

        var completionsCallCount = 0;

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
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
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
            .Returns<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                async (msg, _, onContent, _, _, _, ct) =>
                {
                    msg.Content = "Last assistant response before timeout";
                    onContent?.Invoke(msg.Content);
                    await Task.Delay(TimeSpan.FromSeconds(7), ct);
                });

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — the result should include the last assistant content
        Assert.True(result.Success);
        Assert.Contains("Last assistant response before timeout", result.Result);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutNotExceeded_SubAgentContinues()
    {
        // Arrange — MaxExecutionTimePerSubAgent = 600 (10 minutes), execution is fast.
        // The sub-agent should complete normally without hitting the time limit.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 0,
            MaxIterationsPerSubAgent = 20,
            MaxExecutionTimePerSubAgent = 600 // 10 minutes — plenty of time
        });

        var completionsCallCount = 0;

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
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    // First call returns tool calls, second returns final answer
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
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
                    msg.Content = completionsCallCount == 2 ? "Completed within time limit" : "Working";
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — sub-agent completes normally, time limit not exceeded
        Assert.True(result.Success);
        Assert.Equal("Completed within time limit", result.Result);
        Assert.DoesNotContain("execution time limit", result.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        Assert.Equal(2, completionsCallCount);
    }
}
