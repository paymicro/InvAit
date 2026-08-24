namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    private void SetupProfile(ConnectionProfile profile)
        => _profileManagerMock.Setup(x => x.ActiveProfile).Returns(profile);

    private void SetupStreaming(Action<CompletionsResult, int> onCompletions,
                                Func<int, (string content, Exception? throwEx)> onProcessStream)
    {
        var completionsCallCount = 0;
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(), It.IsAny<string>(), It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (_, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    onCompletions(resultCapture, completionsCallCount);
                })
            .Returns(CreateEmptyDeltaStream());

        var processStreamCallCount = 0;
        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(), It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<List<ToolCall>>>(), It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    processStreamCallCount++;
                    var (content, throwEx) = onProcessStream(processStreamCallCount);
                    if (throwEx is not null)
                        throw throwEx;
                    msg.Content = content;
                    onContent?.Invoke(content);
                })
            .Returns(Task.CompletedTask);
    }

    // --- Transient error classification (Should #5) ---

    [Fact]
    public async Task HttpTimeout_TaskCanceledException_IsRetried_NotTreatedAsCancellation()
    {
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });
        var processStreamCalls = 0;

        SetupStreaming(
            (capture, _) => capture.AccumulatedToolCalls = null,
            call =>
            {
                Interlocked.Increment(ref processStreamCalls);
                // First two attempts mimic an HttpClient timeout: TaskCanceledException
                // while the sub-agent token is NOT cancelled.
                return processStreamCalls <= 2
                    ? ("", new TaskCanceledException())
                    : ("Recovered", null);
            });

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Recovered", result.Result);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(It.IsAny<ConversationSession>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3)); // 2 failed attempts + 1 success
    }

    [Fact]
    public async Task IOException_IsRetried_AsTransient()
    {
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });
        var processStreamCalls = 0;

        SetupStreaming(
            (capture, _) => capture.AccumulatedToolCalls = null,
            call =>
            {
                Interlocked.Increment(ref processStreamCalls);
                return processStreamCalls == 1
                    ? ("", new IOException("connection reset"))
                    : ("Done", null);
            });

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Done", result.Result);
    }

    [Fact]
    public async Task RealCancellation_StillPropagatesImmediately()
    {
        // Regression guard for the widened IsTransientError: a cancellation triggered by
        // the linked token must NOT be swallowed as a retryable transient failure.
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });

        using var parentCts = new CancellationTokenSource();
        SetupStreaming(
            (capture, _) => capture.AccumulatedToolCalls = null,
            _ =>
            {
                // Cancel the parent token mid-stream, then surface its OCE —
                // exactly what a real user cancellation looks like.
                parentCts.Cancel();
                return ("", new OperationCanceledException(parentCts.Token));
            });

        var result = await _executor.ExecuteAsync(args, toolCall, parentCts.Token);

        Assert.False(result.Success);
        Assert.Equal(SubAgentStatus.Cancelled, toolCall.SubAgent!.Status);
        Assert.Equal("Cancelled by user.", toolCall.SubAgent.ErrorMessage);
        // No retry happened: only one LLM call
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(It.IsAny<ConversationSession>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // --- Compression retry (Should #4) ---

    [Fact]
    public async Task Compression_TransientFailure_IsRetried_AndLoopContinues()
    {
        SetupProfile(new ConnectionProfile { TokensToCompress = 50 });
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });

        var compressCalls = 0;
        _chatServiceMock
            .Setup(x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CompletionsResult, CancellationToken>((session, capture, _) =>
            {
                compressCalls++;
                capture.Model = "test-model";
                session.TotalTokens = 30; // below threshold after compression
            })
            .Returns(CreateEmptyDeltaStream());

        var processStreamCalls = 0;
        var completionsCalls = 0;
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(), It.IsAny<string>(), It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (session, _, _, resultCapture, _) =>
                {
                    var call = Interlocked.Increment(ref completionsCalls);
                    resultCapture.Model = "test-model";
                    // First call: above threshold + a tool call so the loop continues;
                    // later calls: below threshold, final text-only response.
                    session.TotalTokens = call == 1 ? 100 : 30;
                    resultCapture.AccumulatedToolCalls = call == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                })
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(), It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<List<ToolCall>>>(), It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    var call = Interlocked.Increment(ref processStreamCalls);
                    return call switch
                    {
                        1 => Turn(msg, onContent, "Working..."),
                        2 => Task.FromException(new HttpRequestException("boom")),   // compression attempt 1 fails transiently
                        3 => Turn(msg, onContent, "Compressed"),                     // compression attempt 2 succeeds
                        _ => Turn(msg, onContent, "Final answer")
                    };
                });

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Final answer", result.Result);
        Assert.Equal(2, compressCalls); // retried once
    }

    [Fact]
    public async Task Compression_NonTransientFailure_DoesNotRetry_ContinuesWithoutCompression()
    {
        SetupProfile(new ConnectionProfile { TokensToCompress = 50 });
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });

        var compressCalls = 0;
        _chatServiceMock
            .Setup(x => x.CompressSessionAsync(
                It.IsAny<ConversationSession>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CompletionsResult, CancellationToken>((_, capture, _) =>
            {
                compressCalls++;
                capture.Model = "test-model";
            })
            .Returns(CreateEmptyDeltaStream());

        var processStreamCalls = 0;
        var completionsCalls = 0;
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(), It.IsAny<string>(), It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (session, _, _, resultCapture, _) =>
                {
                    var call = Interlocked.Increment(ref completionsCalls);
                    resultCapture.Model = "test-model";
                    session.TotalTokens = call == 1 ? 100 : 30;
                    resultCapture.AccumulatedToolCalls = call == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                })
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(), It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<List<ToolCall>>>(), It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    var call = Interlocked.Increment(ref processStreamCalls);
                    return call switch
                    {
                        1 => Turn(msg, onContent, "Working..."),
                        2 => Task.FromException(new InvalidOperationException("fatal")), // non-transient → no retry
                        _ => Turn(msg, onContent, "Final answer")
                    };
                });

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Final answer", result.Result);
        Assert.Equal(1, compressCalls); // non-transient failure → given up immediately
    }

    private static Task Turn(VisualChatMessage msg, Action<string>? onContent, string content)
    {
        msg.Content = content;
        onContent?.Invoke(content);
        return Task.CompletedTask;
    }

    // --- allowedTools validation (Should #6) ---

    [Fact]
    public async Task AllowedTools_AllUnknown_ReturnsFailed_AndNeverStartsSubAgent()
    {
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P", allowedTools = new[] { "nope1", "nope2" } });
        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("allowedTools", result.ErrorMessage);
        Assert.Null(toolCall.SubAgent); // sub-agent never started
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(It.IsAny<ConversationSession>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AllowedTools_PartialUnknown_RunsWithFilteredToolSet()
    {
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new
        {
            task = "T",
            systemPrompt = "P",
            allowedTools = new[] { BuiltInToolEnum.ReadFiles, "nonexistent_tool" }
        });

        Tool[]? captureLastTools = null;
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(), It.IsAny<string>(), It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (_, _, tools, _, _) => captureLastTools = tools.ToArray())
            .Returns(CreateEmptyDeltaStream());
        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(), It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<List<ToolCall>>>(), It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    msg.Content = "Done";
                    onContent?.Invoke("Done");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles),
            CreateTool(BuiltInToolEnum.Grep)
        });

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(captureLastTools);
        var names = captureLastTools!.Select(t => t.Name).ToList();
        Assert.Contains(BuiltInToolEnum.ReadFiles, names);
        Assert.DoesNotContain(BuiltInToolEnum.Grep, names); // not whitelisted → excluded
    }

    // --- Final summary fallback (Should #8) ---

    [Fact]
    public async Task MaxIterations_SummaryCallFails_FallsBackToLastAssistantResponse()
    {
        SetupProfile(new ConnectionProfile { MaxIterationsPerSubAgent = 1 });
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });

        var completionsCalls = 0;
        IEnumerable<Tool>? summaryTools = null;

        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(), It.IsAny<string>(), It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (_, _, tools, resultCapture, _) =>
                {
                    completionsCalls++;
                    resultCapture.Model = "test-model";
                    // Always request a tool call so the loop only ends via maxIterations
                    resultCapture.AccumulatedToolCalls =
                        [new ToolCall { Id = $"tc{completionsCalls}", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }];
                    if (completionsCalls == 2)
                        summaryTools = tools; // the summary call must be text-only (no tools)
                })
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(), It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<List<ToolCall>>>(), It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    if (completionsCalls == 1)
                        return Turn(msg, onContent, "Working...");

                    // Summary call fails non-transiently → fallback path
                    return Task.FromException(new InvalidOperationException("summary failed"));
                });

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("maximum number of iterations", result.Result);
        Assert.Contains("Working...", result.Result); // last assistant content preserved
        Assert.NotNull(summaryTools);
        Assert.Empty(summaryTools!);  // text-only summary request
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
    }

    // --- Hard execution-time limit (Should #7) ---

    [Fact]
    public async Task HardTimeout_MidLlmCall_StopsWithLimitMessage_DistinctFromUserCancel()
    {
        SetupProfile(new ConnectionProfile { MaxExecutionTimePerSubAgent = 1 }); // 1 second
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });

        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(), It.IsAny<string>(), It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(), It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<List<ToolCall>>>(), It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (_, _, _, _, _, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct)); // hangs until the timeout fires

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Same graceful semantics as the between-iteration limit checks: informative
        // message for the LLM, Completed status — and NOT a user cancellation.
        Assert.True(result.Success);
        Assert.Contains("execution time limit", result.Result);
        Assert.DoesNotContain("Cancelled by user", result.Result);
        Assert.Equal(SubAgentStatus.Completed, toolCall.SubAgent!.Status);
    }

    // --- Consistent dynamic limit reads (review finding #8) ---

    [Fact]
    public async Task MaxIterations_ProfileChangeMidRun_IsRespectedOnNextIteration()
    {
        var profile = new ConnectionProfile { MaxIterationsPerSubAgent = 5 };
        SetupProfile(profile);
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });

        var completionsCalls = 0;
        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(), It.IsAny<string>(), It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (_, _, _, resultCapture, _) =>
                {
                    completionsCalls++;
                    resultCapture.Model = "test-model";
                    if (completionsCalls == 1)
                    {
                        // Mid-run profile change: cached value (5) would allow 4 more iterations
                        profile.MaxIterationsPerSubAgent = 1;
                        resultCapture.AccumulatedToolCalls =
                            [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }];
                    }
                    else
                    {
                        resultCapture.AccumulatedToolCalls = null; // summary / final answer
                    }
                })
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(), It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<List<ToolCall>>>(), It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    msg.Content = "Turn done";
                    onContent?.Invoke("Turn done");
                })
            .Returns(Task.CompletedTask);

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        Assert.True(result.Success);
        // iteration 1 + summary call — a stale cached limit of 5 would have produced more calls
        _chatServiceMock.Verify(
            x => x.GetCompletionsForSubAgentAsync(It.IsAny<ConversationSession>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
