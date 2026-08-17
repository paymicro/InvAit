namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_AttachesSubAgentToToolCall()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "You are a tester." });
        SetupChatServiceToReturnContent("Result!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.NotNull(toolCall.SubAgent);
        Assert.Equal("Test task", toolCall.SubAgent!.Task);
        Assert.Equal("You are a tester.", toolCall.SubAgent.SystemPrompt);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentStatus_Completed_OnSuccess()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        Assert.NotNull(toolCall.SubAgent.CompletedAt);
        Assert.Equal("Done!", toolCall.SubAgent.Result);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentStatus_Cancelled_OnCancellation()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var cts = new CancellationTokenSource();

        // Cancel during ProcessStreamAsync execution (not before)
        SetupChatServiceToThrowCancellation(cts.Token);

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, cts.Token);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(SubAgentStatus.Cancelled, toolCall.SubAgent!.Status);
        Assert.NotNull(toolCall.SubAgent.CompletedAt);
        Assert.Contains("Cancelled", toolCall.SubAgent.ErrorMessage!);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentStatus_Failed_OnException()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToThrowException(new InvalidOperationException("LLM error"));

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(SubAgentStatus.Failed, toolCall.SubAgent!.Status);
        Assert.NotNull(toolCall.SubAgent.CompletedAt);
        Assert.Contains("LLM error", toolCall.SubAgent.ErrorMessage!);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentHasMessages()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Final answer");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        var subAgent = toolCall.SubAgent!;
        Assert.NotEmpty(subAgent.Messages);
        // Should have at least: 1 user message (task) + 1 assistant message (final answer)
        Assert.Contains(subAgent.Messages, m => m.Role == ChatMessageRole.User && m.Content == "Test task");
        Assert.Contains(subAgent.Messages, m => m.Role == ChatMessageRole.Assistant);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentIsExpanded_WhileRunning_CollapsedWhenDone()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - should be collapsed after completion
        Assert.False(toolCall.SubAgent!.IsExpanded);
    }
}
