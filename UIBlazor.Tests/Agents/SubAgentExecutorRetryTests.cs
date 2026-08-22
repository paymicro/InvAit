namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Retry logic tests for SubAgentExecutor.RunSubAgentLoopAsync
    //
    //  MaxRetries = 2  →  total attempts = 1 original + 2 retries = 3
    //  Retried: HttpRequestException, TimeoutException, "LLM API error:" prefix
    //  NOT retried: OperationCanceledException, non-transient exceptions
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Helper: sets up ChatService so that ProcessStreamAsync throws on the
    /// first N calls and succeeds on the (N+1)-th call with the given content.
    /// Uses a call counter to sequence throw → success transitions.
    /// </summary>
    private void SetupChatServiceThrowThenSucceed(Exception ex, int throwCount, string successContent)
    {
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
                    processStreamCallCount++;
                    if (processStreamCallCount <= throwCount)
                    {
                        throw ex;
                    }
                    msg.Content = successContent;
                    onContent?.Invoke(successContent);
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());
    }

    /// <summary>
    /// Helper: sets up ChatService so that ProcessStreamAsync sets
    /// resultCapture.Error on the first N calls (triggering "LLM API error:" throw
    /// inside RunSubAgentLoopAsync) and succeeds on the (N+1)-th call.
    /// </summary>
    private void SetupChatServiceApiErrorThenSucceed(string apiError, int errorCount, string successContent)
    {
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
                    processStreamCallCount++;
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = null;
                    // First N calls: set API error → SubAgentExecutor throws "LLM API error: ..."
                    if (processStreamCallCount <= errorCount)
                    {
                        resultCapture.Error = apiError;
                    }
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
                    // Only set content when there's no error
                    if (string.IsNullOrEmpty(resultCapture.Error))
                    {
                        msg.Content = successContent;
                        onContent?.Invoke(successContent);
                    }
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 1: Transient error (HttpRequestException) → retry → success on 2nd attempt
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_RetriesAndSucceedsOnSecondAttempt()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        SetupChatServiceThrowThenSucceed(new HttpRequestException("Connection refused"), 1, "Success after retry");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Success after retry", result.Result);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        // ChatService called twice: 1 failed + 1 successful
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 2: Transient error → retry → success on 3rd attempt (MaxRetries = 2)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_RetriesAndSucceedsOnThirdAttempt()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        // 2 failures, then success on 3rd attempt (MaxRetries = 2, so 3 total attempts)
        SetupChatServiceThrowThenSucceed(new HttpRequestException("503 Service Unavailable"), 2, "Success after 2 retries");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Success after 2 retries", result.Result);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        // ChatService called 3 times: 2 failed + 1 successful
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 3: Transient error → exceeds MaxRetries → failure
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ExceedsMaxRetries_Fails()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        // MaxRetries = 2 → 3 total attempts. All 3 fail.
        SetupChatServiceThrowThenSucceed(new HttpRequestException("Persistent network error"), 3, "Should never reach");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Sub-agent failed", result.ErrorMessage);
        Assert.Contains("Persistent network error", result.ErrorMessage);
        Assert.Equal(SubAgentStatus.Failed, toolCall.SubAgent!.Status);
        // ChatService called exactly 3 times (1 original + 2 retries)
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 4: Non-transient error → immediate failure, no retry
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NonTransientError_FailsImmediatelyWithoutRetry()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        // InvalidOperationException is NOT a transient error → no retry
        SetupChatServiceToThrowException(new InvalidOperationException("Invalid configuration"));

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Sub-agent failed", result.ErrorMessage);
        Assert.Contains("Invalid configuration", result.ErrorMessage);
        Assert.Equal(SubAgentStatus.Failed, toolCall.SubAgent!.Status);
        // ChatService called exactly once — no retries
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 5: OperationCanceledException → immediate failure, no retry
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OperationCanceledException_CancelledImmediatelyWithoutRetry()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        var cts = new CancellationTokenSource();
        cts.Cancel();
        SetupChatServiceToThrowCancellation(cts.Token);

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, cts.Token);

        // Assert
        // With a cancelled token, OperationCanceledException is caught by the
        // `when (cancellationToken.IsCancellationRequested)` filter → Cancelled status
        Assert.False(result.Success);
        Assert.Equal(SubAgentStatus.Cancelled, toolCall.SubAgent!.Status);
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 6: Retry delay is used — retryHandler.GetRetryDelay is called
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TransientError_RetryHandlerGetRetryDelayIsCalled()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        SetupChatServiceThrowThenSucceed(new HttpRequestException("Timeout"), 1, "Recovered");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        // GetRetryDelay should be called once (for attempt 1 → delay before retry)
        _retryHandlerMock.Verify(x => x.GetRetryDelay(1), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TransientError_TwoRetries_RetryHandlerCalledForEachAttempt()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        SetupChatServiceThrowThenSucceed(new HttpRequestException("503"), 2, "Recovered");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        // GetRetryDelay called for attempt 1 and attempt 2
        _retryHandlerMock.Verify(x => x.GetRetryDelay(1), Times.Once);
        _retryHandlerMock.Verify(x => x.GetRetryDelay(2), Times.Once);
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 7: Transient error in the middle of the loop (after a successful iteration)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TransientErrorAfterSuccessfulIteration_RetriesAndCompletes()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });

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
                    // 1st LLM call: return tool_calls (loop continues)
                    // 2nd LLM call: no tool_calls (final answer)
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
                    // Call 1: first iteration — success with tool_calls (content = "Working...")
                    // Call 2: second iteration — transient error (HttpRequestException)
                    // Call 3: second iteration retry — success with final answer
                    switch (processStreamCallCount)
                    {
                        case 1:
                            msg.Content = "Working...";
                            onContent?.Invoke("Working...");
                            break;
                        case 2:
                            throw new HttpRequestException("Transient error mid-loop");
                        case 3:
                            msg.Content = "Final answer after mid-loop retry";
                            onContent?.Invoke("Final answer after mid-loop retry");
                            break;
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
        Assert.Equal("Final answer after mid-loop retry", result.Result);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        // LLM called 2 times (2 distinct iterations), but ProcessStreamAsync called 3 times
        // (1st iteration success + 2nd iteration fail + 2nd iteration retry success)
        // GetCompletionsForSubAgentAsync is called once per attempt (including retries):
        // iteration 1: 1 completions call + 1 process call (success with tool_calls)
        // iteration 2: 1 completions call + 1 process call (fail) + 1 completions call + 1 process call (retry success)
        Assert.Equal(3, completionsCallCount);
        Assert.Equal(3, processStreamCallCount);
        // RetryHandler was called once (for the mid-loop retry)
        _retryHandlerMock.Verify(x => x.GetRetryDelay(1), Times.Once);
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 8: LLM API error (resultCapture.Error) → retry → success
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LlmApiError_RetriesAndSucceeds()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        // First call sets resultCapture.Error = "rate_limit_exceeded"
        // SubAgentExecutor detects this and throws "LLM API error: rate_limit_exceeded"
        // IsTransientError recognizes the "LLM API error:" prefix → retry
        // Second call succeeds
        SetupChatServiceApiErrorThenSucceed("rate_limit_exceeded", 1, "Success after API error retry");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Success after API error retry", result.Result);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        // ChatService called twice: 1 API error + 1 success
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        // RetryHandler was called for the retry
        _retryHandlerMock.Verify(x => x.GetRetryDelay(1), Times.Once);
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 9: LLM API error → exceeds MaxRetries → failure
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LlmApiError_ExceedsMaxRetries_Fails()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        // All 3 attempts return API error (MaxRetries = 2 → 3 total attempts)
        SetupChatServiceApiErrorThenSucceed("internal_server_error", 3, "Should never reach");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Sub-agent failed", result.ErrorMessage);
        Assert.Contains("LLM API error: internal_server_error", result.ErrorMessage);
        Assert.Equal(SubAgentStatus.Failed, toolCall.SubAgent!.Status);
        // ChatService called exactly 3 times
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 10: TimeoutException is treated as transient → retry → success
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TimeoutException_RetriesAndSucceeds()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        SetupChatServiceThrowThenSucceed(new TimeoutException("Request timed out"), 1, "Success after timeout retry");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Success after timeout retry", result.Result);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 11: Retry does NOT occur when MaxRetries is exceeded on first iteration
    //           and the error message is propagated correctly
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AllRetriesExhausted_ErrorMessageContainsOriginalExceptionMessage()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        const string errorMessage = "Connection reset by peer";
        SetupChatServiceThrowThenSucceed(new HttpRequestException(errorMessage), 3, "Never");

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains(errorMessage, result.ErrorMessage);
        // SubAgent.ErrorMessage should also contain the error
        Assert.Contains(errorMessage, toolCall.SubAgent!.ErrorMessage!);
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 12: Partially streamed content is cleaned up on retry
    //           (assistant message from failed attempt is removed from session)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Retry_RemovesFailedAttemptMessageFromSession()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });

        var processStreamCallCount = 0;
        var messagesAfterFirstAttempt = 0;

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
                    processStreamCallCount++;
                    if (processStreamCallCount == 1)
                    {
                        // Partially fill content before throwing
                        msg.Content = "Partial content...";
                        onContent?.Invoke("Partial content...");
                        messagesAfterFirstAttempt = toolCall.SubAgent!.GetMessageCount();
                        throw new HttpRequestException("Connection dropped");
                    }
                    msg.Content = "Full response after retry";
                    onContent?.Invoke("Full response after retry");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Full response after retry", result.Result);
        // After retry, the failed assistant message should have been removed and replaced
        // with a new one. The message count should not have grown unbounded.
        // Initial messages: user + assistant(failed) = 2
        // After retry cleanup: user + assistant(success) = 2
        Assert.Equal(messagesAfterFirstAttempt, toolCall.SubAgent!.GetMessageCount());
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Test 13: OperationCanceledException during retry delay propagates as Cancelled
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CancellationDuringRetryDelay_PropagatesAsCancelled()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test task", systemPrompt = "Prompt" });
        var cts = new CancellationTokenSource();

        // Make GetRetryDelay return a non-zero value so Task.Delay is actually invoked
        _retryHandlerMock
            .Setup(x => x.GetRetryDelay(It.IsAny<int>()))
            .Returns(60); // 60 seconds — will be cancelled long before

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
                (_, _, _, _, _, _, _) =>
                {
                    processStreamCallCount++;
                    // First call throws transient error, triggering retry delay
                    // The cancellation token will be cancelled during Task.Delay
                    throw new HttpRequestException("Transient");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>());

        // Cancel during the retry delay (after the first failure)
        // We use a fire-and-forget task to cancel after a short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            cts.Cancel();
        });

        // Act
        var result = await _executor.ExecuteAsync(args, toolCall, cts.Token);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(SubAgentStatus.Cancelled, toolCall.SubAgent!.Status);
        // Only 1 call to ChatService (the initial failed attempt)
        Assert.Equal(1, processStreamCallCount);
    }
}
