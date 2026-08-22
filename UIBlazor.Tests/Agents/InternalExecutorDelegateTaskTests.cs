namespace UIBlazor.Tests.Agents;

public partial class InternalExecutorTests
{
    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_WithNullToolCall_ReturnsFailure()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Act
        var result = await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, (ToolCall?)null, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("tool call", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_WithToolCall_RoutesToSubAgentExecutor()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var toolCall = new ToolCall();

        var subAgentExecutorMock = new Mock<ISubAgentExecutor>();
        subAgentExecutorMock
            .Setup(x => x.ExecuteAsync(args, toolCall, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult { Success = true, Result = "Sub-agent result" });

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(ISubAgentExecutor)))
            .Returns(subAgentExecutorMock.Object);

        // Act
        var result = await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Sub-agent result", result.Result);
        subAgentExecutorMock.Verify(x => x.ExecuteAsync(args, toolCall, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_WithoutOverload_RoutesToSubAgentExecutorWithNullToolCall()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        var subAgentExecutorMock = new Mock<ISubAgentExecutor>();
        subAgentExecutorMock
            .Setup(x => x.ExecuteAsync(args, null!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult { Success = false, ErrorMessage = "No tool call" });

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(ISubAgentExecutor)))
            .Returns(subAgentExecutorMock.Object);

        // Act - use the overload without toolCall (should pass null)
        var result = await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        // The InternalExecutor checks for null toolCall before routing
        Assert.Contains("tool call", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_WithEmptyTask_PropagatesFailureFromSubAgentExecutor()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "", systemPrompt = "Prompt" });
        var toolCall = new ToolCall();

        var subAgentExecutorMock = new Mock<ISubAgentExecutor>();
        subAgentExecutorMock
            .Setup(x => x.ExecuteAsync(args, toolCall, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult
            {
                Name = BuiltInToolEnum.DelegateTask,
                Success = false,
                ErrorMessage = "delegate_task requires a 'task' parameter."
            });

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(ISubAgentExecutor)))
            .Returns(subAgentExecutorMock.Object);

        // Act
        var result = await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, toolCall, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("delegate_task requires a 'task' parameter.", result.ErrorMessage);
        Assert.Equal(BuiltInToolEnum.DelegateTask, result.Name);
        subAgentExecutorMock.Verify(x => x.ExecuteAsync(args, toolCall, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_Cancellation_PropagatesCancellationToken()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var toolCall = new ToolCall();
        var cts = new CancellationTokenSource();
        var capturedToken = CancellationToken.None;

        var subAgentExecutorMock = new Mock<ISubAgentExecutor>();
        subAgentExecutorMock
            .Setup(x => x.ExecuteAsync(args, toolCall, It.IsAny<CancellationToken>()))
            .Callback<string, ToolCall, CancellationToken>((_, _, ct) =>
            {
                capturedToken = ct;
            })
            .ReturnsAsync(new VsToolResult { Success = false, ErrorMessage = "Operation was cancelled." });

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(ISubAgentExecutor)))
            .Returns(subAgentExecutorMock.Object);

        // Act
        await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, toolCall, cts.Token);

        // Assert - the cancellation token should be passed through to SubAgentExecutor
        Assert.Equal(cts.Token, capturedToken);
    }

    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_WithNullToolCallOverload_PassesNullAndReturnsFailure()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "", systemPrompt = "" });

        // Act - use the overload without toolCall (should pass null to the 4-param overload)
        var result = await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, CancellationToken.None);

        // Assert - InternalExecutor checks for null toolCall before routing to SubAgentExecutor
        Assert.False(result.Success);
        Assert.Contains("tool call", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_WithEmptyTask_PropagatesFailureUsingVsToolResultFailed()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "", systemPrompt = "Prompt" });
        var toolCall = new ToolCall();

        var subAgentExecutorMock = new Mock<ISubAgentExecutor>();
        subAgentExecutorMock
            .Setup(x => x.ExecuteAsync(args, toolCall, It.IsAny<CancellationToken>()))
            .ReturnsAsync(VsToolResult.Failed(BuiltInToolEnum.DelegateTask, "delegate_task requires a 'task' parameter."));

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(ISubAgentExecutor)))
            .Returns(subAgentExecutorMock.Object);

        // Act
        var result = await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, toolCall, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("delegate_task requires a 'task' parameter.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_Cancellation_PropagatesCancelledResult()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var toolCall = new ToolCall();

        var subAgentExecutorMock = new Mock<ISubAgentExecutor>();
        subAgentExecutorMock
            .Setup(x => x.ExecuteAsync(args, toolCall, It.IsAny<CancellationToken>()))
            .ReturnsAsync(VsToolResult.Cancelled(BuiltInToolEnum.DelegateTask));

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(ISubAgentExecutor)))
            .Returns(subAgentExecutorMock.Object);

        // Act
        var result = await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, toolCall, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_WithValidArgs_PassesCorrectArgsToSubAgentExecutor()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Do something specific", systemPrompt = "You are a helper", allowedTools = new[] { "read_files", "grep" } });
        var toolCall = new ToolCall();
        string? capturedArgs = null;

        var subAgentExecutorMock = new Mock<ISubAgentExecutor>();
        subAgentExecutorMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), toolCall, It.IsAny<CancellationToken>()))
            .Callback<string, ToolCall, CancellationToken>((a, _, _) => capturedArgs = a)
            .ReturnsAsync(new VsToolResult { Success = true, Result = "Done" });

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(ISubAgentExecutor)))
            .Returns(subAgentExecutorMock.Object);

        // Act
        await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, toolCall, CancellationToken.None);

        // Assert - the argsJson string should be passed through unchanged
        Assert.Equal(args, capturedArgs);
    }

    [Fact]
    public async Task ExecuteToolAsync_DelegateTask_RoutesToSubAgentExecutorWithCorrectToolCallInstance()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var toolCall = new ToolCall { Id = "call_abc_123", Type = "function" };
        ToolCall? capturedToolCall = null;

        var subAgentExecutorMock = new Mock<ISubAgentExecutor>();
        subAgentExecutorMock
            .Setup(x => x.ExecuteAsync(args, It.IsAny<ToolCall>(), It.IsAny<CancellationToken>()))
            .Callback<string, ToolCall, CancellationToken>((_, tc, _) => capturedToolCall = tc)
            .ReturnsAsync(new VsToolResult { Success = true, Result = "OK" });

        _serviceProviderMock
            .Setup(x => x.GetService(typeof(ISubAgentExecutor)))
            .Returns(subAgentExecutorMock.Object);

        // Act
        await _executor.ExecuteToolAsync(BuiltInToolEnum.DelegateTask, args, toolCall, CancellationToken.None);

        // Assert - the exact same ToolCall instance should be passed through, not a copy
        Assert.Same(toolCall, capturedToolCall);
        Assert.Equal("call_abc_123", capturedToolCall!.Id);
    }
}
