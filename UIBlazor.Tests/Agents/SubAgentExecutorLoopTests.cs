namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WithNoToolCalls_ReturnsContentImmediately()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Direct answer");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Direct answer", result.Result);
        // Should only call LLM once (no tool calls = no loop)
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyContent_ReturnsDefaultMessage()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("empty response", result.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithToolCalls_LoopsUntilNoMoreToolCalls()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Track which iteration we're on
        var processStreamCallCount = 0;
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
                    // First call returns tool calls, second call returns null (no more tool calls)
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
                    processStreamCallCount++;
                    // On the second call (after tool calls were processed), set final content
                    if (processStreamCallCount == 2)
                    {
                        msg.Content = "Final answer after tools";
                        onContent?.Invoke("Final answer after tools");
                    }
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Final answer after tools", result.Result);
        // LLM should be called twice: once for tool calls, once for final answer
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        // Tool calls should be processed once (by the sub-agent's own ToolCallHandler, not the mock)
        // Note: SubAgentExecutor creates its own ToolCallHandler internally, so the mock is not used for tool processing.
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleToolCallsInOneResponse_AllAreProcessed()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        var completionsCallCount = 0;
        var processStreamCallCount = 0;

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
                    // First call: return 3 tool calls at once
                    // Second call: return null (done)
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [
                            new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } },
                            new ToolCall { Id = "tc2", Function = new ToolCallFunction { Name = "grep", Arguments = "{}" } },
                            new ToolCall { Id = "tc3", Function = new ToolCallFunction { Name = "dir", Arguments = "{}" } }
                        ]
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
                    processStreamCallCount++;
                    if (processStreamCallCount == 2)
                    {
                        msg.Content = "All tools processed";
                        onContent?.Invoke("All tools processed");
                    }
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Grep),
            CreateTool(BuiltInToolEnum.Dir)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("All tools processed", result.Result);
        // LLM called twice: once with tool calls, once for final answer
        Assert.Equal(2, completionsCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleToolCalls_AllHaveResultsAfterProcessing()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        var completionsCallCount = 0;
        var processStreamCallCount = 0;
        List<ToolCall>? capturedToolCalls = null;

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
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [
                            new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } },
                            new ToolCall { Id = "tc2", Function = new ToolCallFunction { Name = "grep", Arguments = "{}" } }
                        ]
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
                (msg, _, onContent, onToolCalls, _, resultCapture, _) =>
                {
                    processStreamCallCount++;
                    if (processStreamCallCount == 1)
                    {
                        // The SubAgentExecutor assigns resultCapture.AccumulatedToolCalls to
                        // assistantMessage.ToolCalls AFTER ProcessStreamAsync completes.
                        // During ProcessStreamAsync, tool calls are available via onToolCalls callback
                        // or via resultCapture.AccumulatedToolCalls. Capture from resultCapture.
                        capturedToolCalls = resultCapture.AccumulatedToolCalls;
                    }
                    if (processStreamCallCount == 2)
                    {
                        msg.Content = "Done";
                        onContent?.Invoke("Done");
                    }
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Grep)
        });

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - after tool processing, each tool call should have a result
        Assert.NotNull(capturedToolCalls);
        Assert.Equal(2, capturedToolCalls!.Count);
        // After ProcessToolCallsAsync, each tool call should have a Result set
        Assert.True(capturedToolCalls.All(tc => tc.Result is not null));
    }
}
