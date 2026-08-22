namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WithNullArgs_ReturnsFailure()
    {
        // Act
        var result = await _executor.ExecuteAsync(null!, new ToolCall(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("task", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyArgs_ReturnsFailure()
    {
        // Act
        var result = await _executor.ExecuteAsync("{}", new ToolCall(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("delegate_task requires a 'task' parameter.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyTask_ReturnsFailure()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "", systemPrompt = "test" });

        // Act
        var result = await _executor.ExecuteAsync(args, new ToolCall(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("delegate_task requires a 'task' parameter.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithWhitespaceTask_ReturnsFailure()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "   ", systemPrompt = "test" });

        // Act
        var result = await _executor.ExecuteAsync(args, new ToolCall(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("delegate_task requires a 'task' parameter.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WithTaskButNoSystemPrompt_UsesDefaultPrompt()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Do something" });
        SetupChatServiceToReturnContent("Done!");

        // Act
        var result = await _executor.ExecuteAsync(args, new ToolCall(), CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Done!", result.Result);
    }

    [Fact]
    public async Task ExecuteAsync_CallsPrepareSubAgentSystemPromptAsync_WithCustomPrompt()
    {
        // Arrange
        var toolCall = new ToolCall();
        var customPrompt = "You are a code reviewer. Focus on security issues.";
        var args = JsonSerializer.Serialize(new { task = "Review code", systemPrompt = customPrompt });
        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        _systemPromptBuilderMock.Verify(
            x => x.PrepareSubAgentSystemPromptAsync(customPrompt, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CallsPrepareSubAgentSystemPromptAsync_WithEmptyPrompt_WhenNotProvided()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test" });
        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - should be called with empty string when no systemPrompt provided
        _systemPromptBuilderMock.Verify(
            x => x.PrepareSubAgentSystemPromptAsync(string.Empty, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_UsesFullSystemPrompt_FromSystemPromptBuilder()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Custom prompt" });

        var fullPrompt = "Full system prompt with context and rules";
        _systemPromptBuilderMock
            .Setup(x => x.PrepareSubAgentSystemPromptAsync("Custom prompt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fullPrompt);

        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - the full prompt from SystemPromptBuilder should be passed to GetCompletionsForSubAgentAsync
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.Is<string>(s => s == fullPrompt),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PrepareSubAgentSystemPromptAsync_CalledExactlyOncePerExecution()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - should be called exactly once, not multiple times
        _systemPromptBuilderMock.Verify(
            x => x.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PrepareSubAgentSystemPromptAsync_PassesCancellationToken()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var cts = new CancellationTokenSource();
        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, cts.Token);

        // Assert - the cancellation token should be forwarded
        _systemPromptBuilderMock.Verify(
            x => x.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), cts.Token),
            Times.Once);
    }
}
