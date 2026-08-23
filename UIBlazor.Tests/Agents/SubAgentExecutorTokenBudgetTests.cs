namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_TokenBudgetExceeded_ReturnsBudgetMessage()
    {
        // Arrange — MaxTokensPerSubAgent = 100, session.TotalTokens set to 150 in callback.
        // The token budget check happens at the start of each iteration (before the LLM call).
        // On the second iteration, TotalTokens (150) > MaxTokensPerSubAgent (100) → budget exceeded.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 100,
            MaxIterationsPerSubAgent = 20
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
                (session, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    // First call: return tool calls so the loop continues.
                    // Set TotalTokens above the budget so the check triggers on the next iteration.
                    resultCapture.AccumulatedToolCalls =
                    [
                        new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }
                    ];
                    // Simulate tokens accumulated from the LLM response
                    session.TotalTokens = 150;
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
                    msg.Content = "Working on the task";
                    onContent?.Invoke("Working on the task");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — the result should contain the token budget exceeded message
        Assert.True(result.Success);
        Assert.Contains("token budget", result.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("150", result.Result);
        Assert.Contains("100", result.Result);
    }

    [Fact]
    public async Task ExecuteAsync_TokenBudgetExceeded_StatusIsCompleted()
    {
        // Arrange — when the token budget is exceeded, the sub-agent completes normally
        // (status = Completed, not Failed), because the budget limit is a graceful stop.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 100,
            MaxIterationsPerSubAgent = 20
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
                (session, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls =
                    [
                        new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }
                    ];
                    session.TotalTokens = 150;
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
                    msg.Content = "Processing";
                    onContent?.Invoke("Processing");
                })
            .Returns(Task.CompletedTask);

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
    public async Task ExecuteAsync_TokenBudgetExceeded_IncludesLastResponseInResult()
    {
        // Arrange — when the token budget is exceeded, the result should include
        // the last assistant response content so the main agent has context.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 100,
            MaxIterationsPerSubAgent = 20
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
                (session, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls =
                    [
                        new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }
                    ];
                    session.TotalTokens = 150;
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
                    msg.Content = "Last assistant response before budget exceeded";
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — the result should include the last assistant content
        Assert.True(result.Success);
        Assert.Contains("Last assistant response before budget exceeded", result.Result);
    }

    [Fact]
    public async Task ExecuteAsync_TokenBudgetDisabled_WhenZero_SubAgentCompletesNormally()
    {
        // Arrange — MaxTokensPerSubAgent = 0 means the token budget limit is disabled.
        // Even with high TotalTokens, the sub-agent should continue and complete normally.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 0, // Disabled
            MaxIterationsPerSubAgent = 20
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
                (session, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    // First call returns tool calls, second returns final answer
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                    // Set high token count that WOULD exceed a budget if one were set
                    session.TotalTokens = 99999;
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
                    msg.Content = completionsCallCount == 2 ? "Task completed" : "Working";
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — sub-agent completes normally despite high token count
        Assert.True(result.Success);
        Assert.Equal("Task completed", result.Result);
        Assert.DoesNotContain("token budget", result.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_TokenBudgetNotExceeded_SubAgentContinues()
    {
        // Arrange — MaxTokensPerSubAgent = 1000, tokens = 50 (well within budget).
        // The sub-agent should continue and complete normally.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 1000,
            MaxIterationsPerSubAgent = 20
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
                (session, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    // First call returns tool calls, second returns final answer
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                    // Tokens well within budget
                    session.TotalTokens = 50;
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
                    msg.Content = completionsCallCount == 2 ? "Completed within budget" : "Working";
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — sub-agent completes normally, budget not exceeded
        Assert.True(result.Success);
        Assert.Equal("Completed within budget", result.Result);
        Assert.DoesNotContain("token budget", result.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        // Two LLM calls: one with tool calls, one final answer
        Assert.Equal(2, completionsCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_TokenBudgetExceeded_TokensRecordedOnSubAgent()
    {
        // Arrange — when the token budget is exceeded, the TotalTokens should be
        // recorded on the SubAgentMessage from the session.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 100,
            MaxIterationsPerSubAgent = 20
        });

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
                    resultCapture.AccumulatedToolCalls =
                    [
                        new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }
                    ];
                    session.TotalTokens = 250;
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
                    msg.Content = "Working";
                    onContent?.Invoke("Working");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — TotalTokens from the session should be recorded on the sub-agent
        Assert.Equal(250, toolCall.SubAgent!.TotalTokens);
    }
}
