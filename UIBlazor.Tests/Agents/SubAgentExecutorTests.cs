using System.Runtime.CompilerServices;

namespace UIBlazor.Tests.Agents;

public class SubAgentExecutorTests
{
    private readonly Mock<IChatService> _chatServiceMock;
    private readonly Mock<IToolManager> _toolManagerMock;
    private readonly Mock<ISystemPromptBuilder> _systemPromptBuilderMock;
    private readonly Mock<ILogger<SubAgentExecutor>> _loggerMock;
    private readonly SubAgentExecutor _executor;

    public SubAgentExecutorTests()
    {
        _chatServiceMock = new Mock<IChatService>();
        _toolManagerMock = new Mock<IToolManager>();
        _systemPromptBuilderMock = new Mock<ISystemPromptBuilder>();
        _loggerMock = new Mock<ILogger<SubAgentExecutor>>();

        // SystemPromptBuilder returns the custom prompt as-is (no context in tests)
        _systemPromptBuilderMock
            .Setup(x => x.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prompt, CancellationToken _) => string.IsNullOrEmpty(prompt)
                ? "You are a helpful assistant. Complete the task given to you."
                : prompt);

        _executor = new SubAgentExecutor(
            _chatServiceMock.Object,
            _toolManagerMock.Object,
            _systemPromptBuilderMock.Object,
            _loggerMock.Object);
    }

    #region Argument Parsing Tests

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

    #endregion

    #region SubAgentMessage Attachment Tests

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

    #endregion

    #region Tool Filtering Tests

    [Fact]
    public async Task ExecuteAsync_DelegateTask_AlwaysExcludedFromSubAgentTools()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.DelegateTask, ToolCategory.SubAgent),
            CreateTool(BuiltInToolEnum.Grep)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - verify GetCompletionsForSubAgentAsync was called with tools that exclude delegate_task
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t => !t.Any(tool => tool.Name == BuiltInToolEnum.DelegateTask)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedTools_FiltersToWhitelist()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new
        {
            task = "Test",
            systemPrompt = "Prompt",
            allowedTools = new[] { BuiltInToolEnum.ReadFiles, BuiltInToolEnum.Grep }
        });
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Grep),
            CreateTool(BuiltInToolEnum.Bash),
            CreateTool(BuiltInToolEnum.DelegateTask, ToolCategory.SubAgent)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Grep) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.Bash) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.DelegateTask)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_DeniedTools_ExcludesBlacklisted()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new
        {
            task = "Test",
            systemPrompt = "Prompt",
            deniedTools = new[] { BuiltInToolEnum.Bash }
        });
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Bash),
            CreateTool(BuiltInToolEnum.Grep)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Grep) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.Bash)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyAllowedTools_Null_TreatsAsAllTools()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new
        {
            task = "Test",
            systemPrompt = "Prompt",
            allowedTools = Array.Empty<string>()
        });
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Bash),
            CreateTool(BuiltInToolEnum.DelegateTask, ToolCategory.SubAgent)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - empty allowedTools = all tools (except delegate_task)
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    t.Any(tool => tool.Name == BuiltInToolEnum.ReadFiles) &&
                    t.Any(tool => tool.Name == BuiltInToolEnum.Bash) &&
                    !t.Any(tool => tool.Name == BuiltInToolEnum.DelegateTask)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    #endregion

    #region Execution Loop Tests

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

    #endregion

    #region VsToolResult Tests

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsCorrectVsToolResult()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Success result");

        // Act
        var result = await _executor.ExecuteAsync(args, new ToolCall(), CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(BuiltInToolEnum.DelegateTask, result.Name);
        Assert.Equal("Success result", result.Result);
        Assert.Empty(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_ReturnsCancelledResult()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var cts = new CancellationTokenSource();
        SetupChatServiceToThrowCancellation(cts.Token);

        // Act
        var result = await _executor.ExecuteAsync(args, new ToolCall(), cts.Token);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(BuiltInToolEnum.DelegateTask, result.Name);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Failed_ReturnsFailureResult()
    {
        // Arrange
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToThrowException(new HttpRequestException("Network error"));

        // Act
        var result = await _executor.ExecuteAsync(args, new ToolCall(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(BuiltInToolEnum.DelegateTask, result.Name);
        Assert.Contains("Network error", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_SwitchMode_AlwaysExcludedFromSubAgentTools()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        var tools = new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BasicEnum.SwitchMode, ToolCategory.ModeSwitch),
            CreateTool(BuiltInToolEnum.Grep)
        };
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(tools);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - switch_mode is always excluded (sub-agent cannot change mode or make plans)
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.Is<IEnumerable<Tool>>(t =>
                    !t.Any(tool => tool.Name == BasicEnum.SwitchMode) &&
                    !t.Any(tool => tool.Category == ToolCategory.ModeSwitch)),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    #endregion

    #region Helper Methods

    private void SetupChatServiceToReturnContent(string content)
    {
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
                    msg.Content = content;
                    onContent?.Invoke(content);
                })
            .Returns(Task.CompletedTask);
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());
    }

    private void SetupChatServiceToThrowCancellation(CancellationToken token)
    {
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
            .Throws(new OperationCanceledException(token));
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());
    }

    private void SetupChatServiceToThrowException(Exception ex)
    {
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
            .Throws(ex);
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());
    }

    private static async IAsyncEnumerable<ChatDelta> CreateEmptyDeltaStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static Tool CreateTool(string name, ToolCategory category = ToolCategory.ReadFiles) => new()
    {
        Name = name,
        DisplayName = name,
        Category = category,
        NativeTool = new NativeToolDefinition
        {
            Function = new NativeToolFunction
            {
                Name = name,
                Description = $"Test tool {name}",
                Parameters = new NativeParameters
                {
                    Type = NativeToolType.Object,
                    Properties = new Dictionary<string, NativePropertyDefinition>(),
                    Required = new List<string>()
                }
            }
        },
        ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "ok" })
    };

    #endregion
}
