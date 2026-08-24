namespace UIBlazor.Tests.Agents;

/// <summary>
/// Coverage tests for SubAgentExecutor terminal states and limit-report edge cases.
/// </summary>
public partial class SubAgentExecutorTests
{
    // Finish(): completed/cancelled sub-agents collapse; failures stay expanded
    // so the user can inspect what went wrong without an extra click.

    [Fact]
    public async Task ExecuteAsync_Failed_SubAgentStaysExpanded()
    {
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToThrowException(new InvalidOperationException("LLM error"));

        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        Assert.Equal(SubAgentStatus.Failed, toolCall.SubAgent!.Status);
        Assert.True(toolCall.SubAgent!.IsExpanded);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_SubAgentIsCollapsed()
    {
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var cts = new CancellationTokenSource();
        cts.Cancel();
        SetupChatServiceToThrowCancellation(cts.Token);

        await _executor.ExecuteAsync(args, toolCall, cts.Token);

        Assert.Equal(SubAgentStatus.Cancelled, toolCall.SubAgent!.Status);
        Assert.False(toolCall.SubAgent!.IsExpanded);
    }

    // Budget report when the budget was exceeded before any assistant content exists:
    // iteration 1 returns tool calls (loop continues), iteration 2 hits the budget check
    // before the next LLM call.

    [Fact]
    public async Task ExecuteAsync_TokenBudgetExceeded_NoAssistantContent_ReportsNoResponsePlaceholder()
    {
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        _profileManagerMock.Setup(x => x.ActiveProfile).Returns(new ConnectionProfile
        {
            TokensToCompress = 0,
            MaxTokensPerSubAgent = 1
        });

        var completionsCallCount = 0;
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(), It.IsAny<string>(), It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (session, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    session.TotalTokens = 150; // exceeds the budget of 1
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = BuiltInToolEnum.ReadFiles, Arguments = "{}" } }]
                        : null;
                })
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(), It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask); // intentionally no content

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns([CreateTool(BuiltInToolEnum.ReadFiles)]);

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - budget message on iteration 2, last assistant content is empty → placeholder
        Assert.True(result.Success);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        Assert.Contains("token budget", result.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(no response)", result.Result);
        Assert.Equal(1, completionsCallCount); // no LLM call after the limit was hit
    }
}
