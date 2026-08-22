namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_SubAgentHasItsOwnToolCallHandler()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — ToolCallHandler is kept (not nulled) by ReleaseMemory() in the finally block.
        // Pending approvals are cancelled on it, but the reference is preserved to prevent
        // late-arriving approvals from routing to the wrong (parent) handler.
        Assert.NotNull(toolCall.SubAgent!.ToolCallHandler);
        // The sub-agent should have completed successfully with messages preserved
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        Assert.NotEmpty(toolCall.SubAgent!.GetMessages());
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentToolCallHandler_SeparateFromMainHandler()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        // Create a separate "main agent" handler
        var mainHandler = new ToolCallHandler(_toolManagerMock.Object);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — after completion, the sub-agent's ToolCallHandler is kept (not nulled)
        // by ReleaseMemory(); pending approvals are cancelled on it. The main handler is unaffected.
        Assert.NotNull(toolCall.SubAgent!.ToolCallHandler);
        Assert.NotNull(mainHandler); // Main handler is still alive
    }

    [Fact]
    public async Task ExecuteAsync_CancelPendingApprovalsOnSubAgentHandler_DoesNotAffectMainHandler()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        // Create a main handler with a pending approval
        var mainHandler = new ToolCallHandler(_toolManagerMock.Object);
        var mainToolCall = new ToolCall
        {
            Id = "main-tc-1",
            Function = new ToolCallFunction { Name = "bash", Arguments = "{}" }
        };
        // Set up approval mode to Ask so a waiter is created
        _toolManagerMock.Setup(x => x.GetApprovalModeByToolName("bash")).Returns(ToolApprovalMode.Ask);
        mainHandler.PrepareToolsForApprovals([mainToolCall]);

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // The sub-agent's finally block calls CancelPendingApprovals on the sub-agent's handler
        // This should NOT cancel the main handler's pending approval

        // Assert - main handler's tool call should still be pending (not rejected by sub-agent cleanup)
        // The mainToolCall.ApprovalStatus should still be Pending (not changed by sub-agent cleanup)
        Assert.Equal(ToolApprovalStatus.Pending, mainToolCall.ApprovalStatus);

        // Cleanup
        mainHandler.CancelPendingApprovals();
    }

    [Fact]
    public async Task ExecuteAsync_TwoSubAgentExecutors_HaveSeparateToolCallHandlers()
    {
        // Arrange - create two separate SubAgentExecutor instances
        var executor2 = new SubAgentExecutor(
            _chatServiceMock.Object,
            _toolManagerMock.Object,
            _systemPromptBuilderMock.Object,
            _profileManagerMock.Object,
            _retryHandlerMock.Object,
            _loggerMock.Object);

        var toolCall1 = new ToolCall();
        var toolCall2 = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        SetupChatServiceToReturnContent("Done!");

        // Act - run both executors
        await _executor.ExecuteAsync(args, toolCall1, CancellationToken.None);
        await executor2.ExecuteAsync(args, toolCall2, CancellationToken.None);

        // Assert — after completion, both sub-agents keep their ToolCallHandler reference
        // (ReleaseMemory cancels pending approvals but does NOT null the handler).
        // Each sub-agent still has its own messages.
        Assert.NotNull(toolCall1.SubAgent!.ToolCallHandler);
        Assert.NotNull(toolCall2.SubAgent!.ToolCallHandler);
        Assert.NotSame(toolCall1.SubAgent, toolCall2.SubAgent);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesTemporarySession_NotSavedToChatServiceSession()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Set up a main session on ChatService
        var mainSession = new ConversationSession { Id = "main-session" };
        _chatServiceMock.SetupGet(x => x.Session).Returns(mainSession);

        ConversationSession? subAgentSession = null;
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
                    subAgentSession = session;
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
                    msg.Content = "Done!";
                    onContent?.Invoke("Done!");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.NotNull(subAgentSession);
        Assert.NotSame(mainSession, subAgentSession);
        Assert.NotEqual("main-session", subAgentSession!.Id);
        // Sub-agent session ID should start with "subagent_"
        Assert.StartsWith("subagent_", subAgentSession.Id);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentSession_AlwaysInAgentMode()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        ConversationSession? capturedSession = null;
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
                    capturedSession = session;
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
                    msg.Content = "Done!";
                    onContent?.Invoke("Done!");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedSession);
        Assert.Equal(AppMode.Agent, capturedSession!.Mode);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentSession_HasTaskAsFirstUserMessage()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Do something specific", systemPrompt = "Prompt" });

        ConversationSession? capturedSession = null;
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
                    capturedSession = session;
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
                    msg.Content = "Done!";
                    onContent?.Invoke("Done!");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedSession);
        Assert.NotEmpty(capturedSession!.Messages);
        // First message should be the user task
        var firstMessage = capturedSession.Messages[0];
        Assert.Equal(ChatMessageRole.User, firstMessage.Role);
        Assert.Equal("Do something specific", firstMessage.Content);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentSession_HasUniqueIdAcrossMultipleExecutions()
    {
        // Arrange
        var toolCall1 = new ToolCall();
        var toolCall2 = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        var capturedSessions = new List<ConversationSession>();

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
                    msg.Content = "Done!";
                    onContent?.Invoke("Done!");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act - run two sub-agent executions
        await _executor.ExecuteAsync(args, toolCall1, CancellationToken.None);
        await _executor.ExecuteAsync(args, toolCall2, CancellationToken.None);

        // Assert - each execution should create a unique session
        Assert.Equal(2, capturedSessions.Count);
        Assert.NotEqual(capturedSessions[0].Id, capturedSessions[1].Id);
        Assert.StartsWith("subagent_", capturedSessions[0].Id);
        Assert.StartsWith("subagent_", capturedSessions[1].Id);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentSession_NotSameAsChatServiceSession_EvenWhenMainSessionExists()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Set up a main session with messages and tokens
        var mainSession = new ConversationSession
        {
            Id = "main-session-id",
            Mode = AppMode.Chat,
            TotalTokens = 500
        };
        mainSession.Messages.Add(new VisualChatMessage { Content = "Main session message", Role = ChatMessageRole.User });
        _chatServiceMock.SetupGet(x => x.Session).Returns(mainSession);

        ConversationSession? capturedSession = null;
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
                    capturedSession = session;
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
                    msg.Content = "Done!";
                    onContent?.Invoke("Done!");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - sub-agent session should be completely separate from main session
        Assert.NotNull(capturedSession);
        Assert.NotSame(mainSession, capturedSession);
        Assert.NotEqual("main-session-id", capturedSession!.Id);
        Assert.NotEqual(mainSession.TotalTokens, capturedSession.TotalTokens);
        Assert.NotEqual(mainSession.Messages.Count, capturedSession.Messages.Count);
        Assert.Equal(AppMode.Agent, capturedSession.Mode);
        Assert.NotEqual(AppMode.Chat, capturedSession.Mode);
    }

    [Fact]
    public async Task ExecuteAsync_ReleaseMemory_PreservesMessagesAndContent()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert — ReleaseMemory() was called in the finally block:
        // 1. ToolCallHandler is kept (not nulled); pending approvals cancelled on it
        Assert.NotNull(toolCall.SubAgent!.ToolCallHandler);

        // 2. PendingToolCallId is cleared
        Assert.Null(toolCall.SubAgent!.PendingToolCallId);

        // 3. Messages are PRESERVED (not deleted)
        var messages = toolCall.SubAgent!.GetMessages();
        Assert.NotEmpty(messages);

        // 4. Content and ReasoningContent are PRESERVED in messages
        //    (SubAgentView displays these via MarkdownBlock)
        var assistantMsg = messages.FirstOrDefault(m => m.Role == ChatMessageRole.Assistant);
        Assert.NotNull(assistantMsg);
        Assert.Equal("Done!", assistantMsg!.Content);

        // 5. Segments are cleared (not used by SubAgentView — only by MessageContent.razor)
        Assert.All(messages, m => Assert.Empty(m.Segments));

        // 6. Transient flags are reset
        Assert.All(messages, m => Assert.False(m.IsStreaming));
        Assert.All(messages, m => Assert.False(m.IsShouldRender));

        // 7. Status and Result are preserved
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        Assert.Equal("Done!", toolCall.SubAgent!.Result);
    }
}
