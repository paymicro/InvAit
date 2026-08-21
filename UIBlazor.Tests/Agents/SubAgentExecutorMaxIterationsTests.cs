namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ReachingMaxIterations_ReturnsMaxIterationsMessage()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

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
                    // Always return tool_calls so the loop never terminates naturally
                    resultCapture.AccumulatedToolCalls =
                    [
                        new ToolCall { Id = $"tc{completionsCallCount}", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }
                    ];
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
                (msg, _, onContent, _, _, resultCapture, _) =>
                {
                    // The 21st call is the final summary request — return text without tool calls
                    if (completionsCallCount == 21)
                    {
                        resultCapture.AccumulatedToolCalls = null;
                        msg.Content = "Summary of work done";
                    }
                    else
                    {
                        msg.Content = $"Iteration output {completionsCallCount}";
                    }
                    onContent?.Invoke(msg.Content);
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
        // The result should be the final summary, not the old max-iterations message
        Assert.Contains("Summary of work done", result.Result);
        // LLM should be called MaxIterations (20) + 1 final summary = 21 times
        Assert.Equal(21, completionsCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReachingMaxIterations_StatusIsCompleted()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        var callCount = 0;

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
                    callCount++;
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls =
                    [
                        new ToolCall { Id = $"tc{callCount}", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }
                    ];
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
                    msg.Content = "working";
                    onContent?.Invoke("working");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - even when hitting max iterations, status is Completed (not Failed)
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_MaxIterations_IncludesLastAssistantContentInResult()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

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
                    resultCapture.AccumulatedToolCalls =
                    [
                        new ToolCall { Id = $"tc{completionsCallCount}", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }
                    ];
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
                (msg, _, onContent, _, _, resultCapture, _) =>
                {
                    // The 21st call is the final summary — return text without tool calls
                    if (completionsCallCount == 21)
                    {
                        resultCapture.AccumulatedToolCalls = null;
                        msg.Content = "Final summary of work accomplished";
                    }
                    else
                    {
                        msg.Content = completionsCallCount == 20 ? "Last iteration output" : "working";
                    }
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - the result should be the final summary from the 21st LLM call
        Assert.True(result.Success);
        Assert.Contains("Final summary of work accomplished", result.Result);
    }

    [Fact]
    public async Task ExecuteAsync_MaxIterations_TokensRecordedOnSubAgent()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        var completionsCallCount = 0;

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
                    // The 21st call is the final summary — no tool calls
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 21
                        ? null
                        : [new ToolCall { Id = $"tc{completionsCallCount}", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }];
                    // Accumulate tokens across iterations
                    session.TotalTokens = completionsCallCount * 50;
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
                    msg.Content = "working";
                    onContent?.Invoke("working");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - TotalTokens should be recorded from the session (21 * 50 = 1050)
        // 20 iterations + 1 final summary call
        Assert.Equal(1050, toolCall.SubAgent!.TotalTokens);
    }
}
