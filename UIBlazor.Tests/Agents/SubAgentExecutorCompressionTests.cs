namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_TriggersCompression_WhenTokensExceedThreshold()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Set up profile with compression threshold = 50
        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 50 });

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
                (session, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    // First call: return tool_calls (so loop continues) + set tokens above threshold
                    // Second call (after compression): return no tool_calls + set tokens below threshold
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                    session.TotalTokens = completionsCallCount == 1 ? 100 : 30;
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
                    // Call 1: initial LLM response with tool_calls (content = "Working...")
                    // Call 2: compression stream (content = "Compressed...")
                    // Call 3: final LLM response after compression (content = "Final answer")
                    msg.Content = processStreamCallCount switch
                    {
                        1 => "Working...",
                        2 => "Compressed context",
                        _ => "Final answer"
                    };
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        // Mock CompressSessionAsync to reduce tokens
        _chatServiceMock
            .Setup(x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CompletionsResult, CancellationToken>(
                (session, resultCapture, _) =>
                {
                    resultCapture.Model = "test-model";
                    // Simulate compression reducing token count
                    session.TotalTokens = 30;
                })
            .Returns(CreateEmptyDeltaStream());

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Final answer", result.Result);
        // Compression should have been called once (on iteration 2, before the second LLM call)
        _chatServiceMock.Verify(
            x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoCompression_WhenTokensBelowThreshold()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Profile with high threshold
        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 10000 });

        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - compression should NOT be called
        _chatServiceMock.Verify(
            x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NoCompression_WhenTokensToCompressIsZero()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Profile with compression disabled (default)
        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 0 });

        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        _chatServiceMock.Verify(
            x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CompressionFailure_ContinuesWithoutCompression()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 50 });

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
                (session, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    // First call: return tool_calls + set tokens above threshold
                    // Second call: return no tool_calls + set tokens below threshold
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                    session.TotalTokens = completionsCallCount == 1 ? 100 : 30;
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
                        // This is the compression ProcessStreamAsync call — throw to simulate failure
                        throw new HttpRequestException("Compression API error");
                    }
                    msg.Content = processStreamCallCount == 1 ? "Working..." : "Final answer";
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        // Mock CompressSessionAsync to return an empty stream
        _chatServiceMock
            .Setup(x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateEmptyDeltaStream());

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - sub-agent should still complete successfully despite compression failure
        Assert.True(result.Success);
        Assert.Equal("Final answer", result.Result);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_IsCompressing_SetTrueDuringCompression_SetFalseAfter()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 50 });

        var completionsCallCount = 0;
        var processStreamCallCount = 0;
        var isCompressingDuringCompression = false;

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
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                    session.TotalTokens = completionsCallCount == 1 ? 100 : 30;
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
                        // This is the compression stream — check IsCompressing
                        isCompressingDuringCompression = toolCall.SubAgent!.IsCompressing;
                    }
                    msg.Content = processStreamCallCount switch
                    {
                        1 => "Working...",
                        2 => "Compressed",
                        _ => "Final answer"
                    };
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        _chatServiceMock
            .Setup(x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CompletionsResult, CancellationToken>(
                (session, resultCapture, _) =>
                {
                    resultCapture.Model = "test-model";
                    session.TotalTokens = 30;
                })
            .Returns(CreateEmptyDeltaStream());

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(isCompressingDuringCompression, "IsCompressing should be true during compression");
        Assert.False(toolCall.SubAgent!.IsCompressing, "IsCompressing should be false after compression");
    }

    [Fact]
    public async Task ExecuteAsync_IsCompressing_SetFalseAfterCompressionFailure()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 50 });

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
                (session, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                    session.TotalTokens = completionsCallCount == 1 ? 100 : 30;
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
                        // Compression stream — throw to simulate failure
                        throw new HttpRequestException("Compression API error");
                    }
                    msg.Content = processStreamCallCount == 1 ? "Working..." : "Final answer";
                    onContent?.Invoke(msg.Content);
                })
            .Returns(Task.CompletedTask);

        _chatServiceMock
            .Setup(x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateEmptyDeltaStream());

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - IsCompressing should be false even after compression failure
        Assert.False(toolCall.SubAgent!.IsCompressing);
    }

    [Fact]
    public async Task ExecuteAsync_IsCompressing_NeverSetWhenNoCompressionNeeded()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        // Compression disabled
        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 0 });

        SetupChatServiceToReturnContent("Done!");

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - IsCompressing should never have been set to true
        Assert.False(toolCall.SubAgent!.IsCompressing);
    }

    [Fact]
    public async Task ExecuteAsync_CompressionCancellation_RemovesCompressionMessageAndPropagatesCancellation()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var cts = new CancellationTokenSource();

        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 50 });

        var completionsCallCount = 0;
        var processStreamCallCount = 0;
        var messagesBeforeCancellation = 0;

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
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                    session.TotalTokens = 100; // Always above threshold
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
                (msg, _, onContent, _, _, _, ct) =>
                {
                    processStreamCallCount++;
                    if (processStreamCallCount == 2)
                    {
                        // This is the compression stream — cancel during compression
                        messagesBeforeCancellation = toolCall.SubAgent!.Messages.Count;
                        cts.Cancel();
                        throw new OperationCanceledException(ct);
                    }
                    msg.Content = "Working...";
                    onContent?.Invoke("Working...");
                })
            .Returns(Task.CompletedTask);

        _chatServiceMock
            .Setup(x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateEmptyDeltaStream());

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, cts.Token);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(SubAgentStatus.Cancelled, toolCall.SubAgent!.Status);
        // The compression message should have been removed
        Assert.True(toolCall.SubAgent!.Messages.Count < messagesBeforeCancellation + 1,
            "Compression message should be removed after cancellation");
    }

    [Fact]
    public async Task ExecuteAsync_ExceedsThresholdMultipleTimes_CompressesMultipleTimes()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 50 });

        var completionsCallCount = 0;
        var processStreamCallCount = 0;
        var compressionCallCount = 0;

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
                    // Iterations 1 and 2: return tool_calls and set tokens above threshold
                    // Iteration 3: return no tool_calls and set tokens below threshold
                    resultCapture.AccumulatedToolCalls = completionsCallCount <= 2
                        ? [new ToolCall { Id = $"tc{completionsCallCount}", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                    // Always set tokens above threshold (except last iteration)
                    session.TotalTokens = completionsCallCount <= 2 ? 100 : 30;
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
                    // Odd calls (1, 3, 5) are LLM responses, even calls (2, 4) are compression streams
                    if (processStreamCallCount % 2 == 1)
                    {
                        msg.Content = processStreamCallCount == 5 ? "Final answer" : "Working...";
                        onContent?.Invoke(msg.Content);
                    }
                    else
                    {
                        msg.Content = "Compressed";
                        onContent?.Invoke("Compressed");
                    }
                })
            .Returns(Task.CompletedTask);

        _chatServiceMock
            .Setup(x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CompletionsResult, CancellationToken>(
                (session, resultCapture, _) =>
                {
                    compressionCallCount++;
                    resultCapture.Model = "test-model";
                    // Compression reduces tokens, but they'll be set back to 100 on next LLM call
                    session.TotalTokens = 30;
                })
            .Returns(CreateEmptyDeltaStream());

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Final answer", result.Result);
        // Compression should have been called twice (before iteration 2 and before iteration 3)
        Assert.Equal(2, compressionCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ThreeCompressions_CompressesThreeTimes()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        _profileManagerMock
            .Setup(x => x.ActiveProfile)
            .Returns(new ConnectionProfile { TokensToCompress = 50 });

        var completionsCallCount = 0;
        var processStreamCallCount = 0;
        var compressionCallCount = 0;

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
                    // Iterations 1-3: return tool_calls and set tokens above threshold
                    // Iteration 4: return no tool_calls and set tokens below threshold
                    resultCapture.AccumulatedToolCalls = completionsCallCount <= 3
                        ? [new ToolCall { Id = $"tc{completionsCallCount}", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                    session.TotalTokens = completionsCallCount <= 3 ? 100 : 30;
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
                    // Odd calls (1, 3, 5, 7) are LLM responses, even calls (2, 4, 6) are compression streams
                    if (processStreamCallCount % 2 == 1)
                    {
                        msg.Content = processStreamCallCount == 7 ? "Final answer" : "Working...";
                        onContent?.Invoke(msg.Content);
                    }
                    else
                    {
                        msg.Content = "Compressed";
                        onContent?.Invoke("Compressed");
                    }
                })
            .Returns(Task.CompletedTask);

        _chatServiceMock
            .Setup(x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CompletionsResult, CancellationToken>(
                (session, resultCapture, _) =>
                {
                    compressionCallCount++;
                    resultCapture.Model = "test-model";
                    session.TotalTokens = 30;
                })
            .Returns(CreateEmptyDeltaStream());

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Final answer", result.Result);
        // Compression should have been called 3 times
        Assert.Equal(3, compressionCallCount);
        // LLM should have been called 4 times (3 with tool calls + 1 final)
        Assert.Equal(4, completionsCallCount);
    }
}
