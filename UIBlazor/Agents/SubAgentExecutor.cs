using System.Diagnostics;

namespace UIBlazor.Agents;

/// <summary>
/// Executes sub-agent tasks delegated by the main agent via the delegate_task tool.
/// A sub-agent runs in Agent mode with its own system prompt, conversation session,
/// filtered tool set and ToolCallHandler (approval waiters are isolated from the main agent).
/// It can neither delegate recursively (SubAgent category excluded) nor switch modes.
/// </summary>
public class SubAgentExecutor(
    IChatService chatService,
    IToolManager toolManager,
    ISystemPromptBuilder systemPromptBuilder,
    IProfileManager profileManager,
    IRetryHandler retryHandler,
    ILogger<SubAgentExecutor> logger) : ISubAgentExecutor
{
    private const int MaxRetries = 2;

    /// <summary>Total attempts for a single compression call before giving up (non-fatal).</summary>
    private const int MaxCompressionAttempts = 2;

    private const string EmptyResponse = "(Sub-agent returned an empty response.)";

    private const string SummaryInstruction =
        "You are about to reach your iteration limit. " +
        "Provide a comprehensive summary of what you have accomplished so far, " +
        "what remains to be done, and any important findings or decisions. " +
        "This is your final response.";

    /// <summary>Non-positive limits disable the corresponding check.</summary>
    private static readonly HashSet<ToolCategory> ExcludedCategories =
        [ToolCategory.SubAgent, ToolCategory.ModeSwitch];

    public event Action<SubAgentMessage>? SubAgentStateChanged;

    public async Task<VsToolResult> ExecuteAsync(string argsJson, ToolCall toolCall, CancellationToken cancellationToken)
    {
        var args = JsonUtils.DeserializeParameters(argsJson ?? "{}");
        var task = GetArg(args, "task");
        var systemPrompt = GetArg(args, "systemPrompt");

        if (task.Length == 0)
            return VsToolResult.Failed(BuiltInToolEnum.DelegateTask, "delegate_task requires a 'task' parameter.");

        var allowedTools = ParseStringArray(args, "allowedTools");

        // Fail fast when the whitelist matches nothing — the sub-agent would have zero tools
        // and could not accomplish anything. Returning an error lets the LLM correct the names.
        if (allowedTools is { Length: > 0 })
        {
            var availableNames = toolManager.GetEnabledTools(AppMode.Agent)
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = allowedTools.Where(n => !availableNames.Contains(n)).ToArray();
            if (unknown.Length > 0)
                logger.LogWarning("delegate_task allowedTools contains unknown tools: {UnknownTools}", string.Join(", ", unknown));

            if (BuildSubAgentTools(allowedTools).FirstOrDefault() is null)
            {
                logger.LogWarning("delegate_task allowedTools matched no available Agent-mode tools: {AllowedTools}",
                    string.Join(", ", allowedTools));
                return VsToolResult.Failed(BuiltInToolEnum.DelegateTask,
                    $"None of the requested allowedTools are available: {string.Join(", ", allowedTools)}. " +
                    "Retry without allowedTools or use exact names of available tools.");
            }
        }

        var fullSystemPrompt = await systemPromptBuilder.PrepareSubAgentSystemPromptAsync(systemPrompt, cancellationToken);
        var tools = BuildSubAgentTools(allowedTools).ToList();

        var handler = new ToolCallHandler(toolManager);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Hard cap on total execution time: a single long-running LLM call must not outlive
        // the configured limit. Distinguished from user cancellation in the catch below.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
        var timeLimitSeconds = profileManager.ActiveProfile.MaxExecutionTimePerSubAgent;
        if (timeLimitSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeLimitSeconds));

        var subAgent = new SubAgentMessage
        {
            Task = task,
            SystemPrompt = systemPrompt,
            AllowedTools = allowedTools,
            Status = SubAgentStatus.Running,
            IsExpanded = true, // auto-expand while running so the user sees progress
            ToolCallHandler = handler,
            MaxRetryAttempts = MaxRetries + 1,
        };
        subAgent.SetCancellationTokenSource(timeoutCts);
        handler.ApprovalRequired += id => OnApprovalRequired(subAgent, id);
        toolCall.SubAgent = subAgent;

        ConversationSession? session = null;
        try
        {
            // Initial structural notification: AiChat re-renders and ToolCallBlock subscribes
            // to subAgent.StateChanged. Streaming tokens intentionally do NOT raise it —
            // SubAgentView throttles its own rendering via StateChanged.
            NotifyStructuralChange(subAgent);

            session = new ConversationSession
            {
                Id = $"subagent_{DateTime.Now:s}_{Guid.NewGuid():N}",
                Mode = AppMode.Agent
            };
            subAgent.AttachSession(session); // message mutations now propagate to the session automatically
            AddUserMessage(subAgent, task);

            logger.LogInformation("Sub-agent started. Task: {Task}", task);

            var result = await RunLoopAsync(session, subAgent, fullSystemPrompt, tools, handler, timeoutCts.Token);

            subAgent.Result = result;
            SyncTokens(subAgent, session);
            Finish(subAgent, SubAgentStatus.Completed);

            logger.LogInformation("Sub-agent completed. Tokens: {Tokens}", session.TotalTokens);
            return new VsToolResult { Name = BuiltInToolEnum.DelegateTask, Result = result };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // linkedCts fires only on real user/chat-level cancellation; if it did not fire,
            // the timeout chain (CancelAfter) is the cause.
            if (linkedCts.Token.IsCancellationRequested)
            {
                logger.LogInformation("Sub-agent cancelled.");
                Finish(subAgent, SubAgentStatus.Cancelled, "Cancelled by user.");
                return VsToolResult.Cancelled(BuiltInToolEnum.DelegateTask);
            }

            // Hard execution-time limit fired mid-LLM-call (between-iteration checks missed it).
            // Same graceful semantics as the between-iteration limit checks: a Completed status
            // with an informative report for the parent agent.
            var lastResponse = session is null ? "(no response)" : LastAssistantContent(session);
            var message = $"Sub-agent exceeded execution time limit " +
                          $"({(DateTime.Now - subAgent.StartedAt).TotalMinutes:F1} / {timeLimitSeconds / 60.0:F0} minutes). " +
                          $"Last response: {lastResponse}";
            logger.LogInformation("Sub-agent exceeded execution time limit mid-call.");
            Finish(subAgent, SubAgentStatus.Completed);
            return new VsToolResult { Name = BuiltInToolEnum.DelegateTask, Result = message };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sub-agent failed.");
            Finish(subAgent, SubAgentStatus.Failed, ex.Message);
            return VsToolResult.Failed(BuiltInToolEnum.DelegateTask, $"Sub-agent failed: {ex.Message}");
        }
        finally
        {
            handler.CancelPendingApprovals();
            subAgent.ReleaseMemory(); // also disposes the CTS taken from SetCancellationTokenSource
        }
    }

    /// <summary>
    /// Stream LLM response → process tool calls → repeat until done or a limit is hit.
    /// All limits are re-read from the active profile on every iteration for consistency.
    /// </summary>
    private async Task<string> RunLoopAsync(
        ConversationSession session,
        SubAgentMessage subAgent,
        string systemPrompt,
        IEnumerable<Tool> tools,
        IToolCallHandler handler,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var iteration = 0;

        while (iteration < GetMaxIterations() && !cancellationToken.IsCancellationRequested)
        {
            if (GetLimitExceededMessage(session, startedAt) is { } limitMessage)
                return limitMessage;

            iteration++;

            if (NeedCompression(session))
            {
                await CompressContextAsync(session, subAgent, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            var assistant = await StreamAssistantWithRetryAsync(session, subAgent, systemPrompt, tools, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                break;

            if (assistant.ToolCalls is not { Count: > 0 })
                return assistant.Content.Length > 0 ? assistant.Content : EmptyResponse;

            handler.PrepareToolsForApprovals(assistant.ToolCalls);
            assistant.IsShouldRender = true;
            subAgent.NotifyStateChanged();

            await handler.ProcessToolCallsAsync(assistant.ToolCalls, cancellationToken);

            // TotalTokens is already maintained by ChatService from usage data — just mirror it.
            SyncTokens(subAgent, session);
            subAgent.NotifyStateChanged();
        }

        // Cancellation wins over the iteration limit: don't spend another LLM call on a summary.
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("Sub-agent was cancelled.");

        return await RequestFinalSummaryAsync(session, subAgent, systemPrompt, tools, cancellationToken);
    }

    /// <summary>
    /// Streams one assistant turn, retrying transient LLM errors up to MaxRetries times.
    /// Each failed attempt's message is removed and session tokens are rolled back to the
    /// pre-attempt snapshot (session.Messages.Remove alone does not update TotalTokens).
    /// </summary>
    private async Task<VisualChatMessage> StreamAssistantWithRetryAsync(
        ConversationSession session,
        SubAgentMessage subAgent,
        string systemPrompt,
        IEnumerable<Tool> tools,
        CancellationToken cancellationToken)
    {
        VisualChatMessage? assistant = null;
        var tokensBeforeAttempt = session.TotalTokens;

        for (var attempt = 0; ; attempt++)
        {
            if (assistant is not null)
            {
                subAgent.RemoveMessage(assistant); // subtracts its token contribution first...
                session.TotalTokens = tokensBeforeAttempt; // ...then restores the absolute snapshot
            }

            assistant = VisualChatMessage.CreateStreaming();
            subAgent.AddMessage(assistant);
            subAgent.NotifyStateChanged();

            var capture = new CompletionsResult();
            try
            {
                await StreamAsync(session, subAgent, assistant, capture,
                    chatService.GetCompletionsForSubAgentAsync(session, systemPrompt, tools, capture, cancellationToken),
                    cancellationToken);

                if (!string.IsNullOrEmpty(capture.Error))
                    throw new LlmApiException($"LLM API error: {capture.Error}");

                assistant.ToolCalls = capture.AccumulatedToolCalls;
                assistant.IsStreaming = false;
                SyncTokens(subAgent, session);
                subAgent.NotifyStateChanged();
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                assistant.IsStreaming = false;
                throw;
            }
            catch (Exception ex) when (IsTransientError(ex) && attempt < MaxRetries)
            {
                await WaitForRetryAsync(subAgent, ex, attempt, cancellationToken);
            }
        }

        return assistant!;
    }

    /// <summary>
    /// Single wrapper over ChatService.ProcessStreamAsync used for all sub-agent LLM calls.
    /// Keeps the UI flag / token counter in sync on every delta without duplicating callbacks.
    /// </summary>
    private async Task StreamAsync(
        ConversationSession session,
        SubAgentMessage subAgent,
        VisualChatMessage message,
        CompletionsResult capture,
        IAsyncEnumerable<ChatDelta> deltas,
        CancellationToken cancellationToken)
    {
        await chatService.ProcessStreamAsync(
            message,
            deltas,
            onContentUpdate: _ =>
            {
                message.IsShouldRender = true;
                SyncTokens(subAgent, session);
                subAgent.NotifyStateChanged();
            },
            onToolCallsUpdate: toolCalls =>
            {
                message.ToolCalls = toolCalls;
                message.IsShouldRender = true;
                SyncTokens(subAgent, session);
                subAgent.NotifyStateChanged();
            },
            onStateChange: () =>
            {
                message.Model ??= capture.Model;
                SyncTokens(subAgent, session);
            },
            capture,
            cancellationToken);
    }

    private async Task WaitForRetryAsync(SubAgentMessage subAgent, Exception ex, int attempt, CancellationToken cancellationToken)
    {
        var delaySeconds = retryHandler.GetRetryDelay(attempt + 1);
        logger.LogWarning(ex, "Sub-agent LLM call failed (attempt {Attempt}/{Total}). Retrying in {Delay}s.",
            attempt + 1, MaxRetries + 1, delaySeconds);

        subAgent.IsRetrying = true;
        subAgent.RetryAttempt = attempt + 1;
        subAgent.RetryDelaySeconds = delaySeconds;
        subAgent.RetryCountdown = delaySeconds;
        subAgent.NotifyStateChanged();

        try
        {
            // Notify on every tick: nothing else triggers a re-render while the LLM call
            // is between attempts, so without notifications the countdown badge would freeze.
            await retryHandler.WaitForRetryAsync(delaySeconds, i =>
            {
                subAgent.RetryCountdown = i;
                subAgent.NotifyStateChanged();
            }, cancellationToken);
        }
        finally
        {
            subAgent.IsRetrying = false;
            subAgent.RetryCountdown = 0;
            subAgent.NotifyStateChanged();
        }
    }

    private bool NeedCompression(ConversationSession session)
        => profileManager.ActiveProfile.TokensToCompress > 0
           && session.TotalTokens > profileManager.ActiveProfile.TokensToCompress;

    /// <summary>Re-read per iteration so mid-execution profile changes behave consistently.</summary>
    private int GetMaxIterations()
    {
        var max = profileManager.ActiveProfile.MaxIterationsPerSubAgent;
        return max > 0 ? max : int.MaxValue;
    }

    /// <summary>
    /// Compression failure is non-fatal: the loop continues with the uncompressed context.
    /// Transient errors are retried up to <see cref="MaxCompressionAttempts"/> times.
    /// </summary>
    private async Task CompressContextAsync(ConversationSession session, SubAgentMessage subAgent, CancellationToken cancellationToken)
    {
        logger.LogInformation("Sub-agent context compression started. Tokens: {Tokens}", session.TotalTokens);

        subAgent.IsCompressing = true;
        subAgent.NotifyStateChanged();

        var capture = new CompletionsResult();
        var compressMessage = VisualChatMessage.CreateStreaming("## ♻ Context compression...\n\n");
        subAgent.AddMessage(compressMessage);
        subAgent.NotifyStateChanged();

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await StreamAsync(session, subAgent, compressMessage, capture,
                        chatService.CompressSessionAsync(session, capture, cancellationToken), cancellationToken);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < MaxCompressionAttempts && IsTransientError(ex))
                {
                    logger.LogWarning(ex, "Sub-agent context compression failed (attempt {Attempt}/{Total}). Retrying.",
                        attempt, MaxCompressionAttempts);
                    compressMessage.Content = string.Empty;
                }
            }

            // Compression rewrote the session history — mirror it back into the sub-agent messages.
            subAgent.SetMessages(session.GetMessagesSnapshot());
            logger.LogInformation("Sub-agent context compression completed. Tokens after: {Tokens}", session.TotalTokens);
        }
        catch (OperationCanceledException)
        {
            subAgent.RemoveMessage(compressMessage);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sub-agent context compression failed. Continuing with current context.");
            subAgent.RemoveMessage(compressMessage);
        }
        finally
        {
            compressMessage.IsStreaming = false;
            subAgent.IsCompressing = false;
            SyncTokens(subAgent, session);
            subAgent.NotifyStateChanged();
        }
    }

    /// <summary>Final text-only LLM call after maxIterations; falls back to the raw last response on error.</summary>
    private async Task<string> RequestFinalSummaryAsync(
        ConversationSession session,
        SubAgentMessage subAgent,
        string systemPrompt,
        IEnumerable<Tool> tools,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sub-agent reached max iterations ({MaxIterations}). Requesting final summary.",
            profileManager.ActiveProfile.MaxIterationsPerSubAgent);

        var requestMessage = AddUserMessage(subAgent, SummaryInstruction);
        var summaryMessage = VisualChatMessage.CreateStreaming();
        subAgent.AddMessage(summaryMessage);
        subAgent.NotifyStateChanged();

        var capture = new CompletionsResult();
        try
        {
            await StreamAsync(session, subAgent, summaryMessage, capture,
                chatService.GetCompletionsForSubAgentAsync(session, systemPrompt, [], capture, cancellationToken),
                cancellationToken);

            summaryMessage.ToolCalls = capture.AccumulatedToolCalls;
            summaryMessage.IsStreaming = false;
            SyncTokens(subAgent, session);
            subAgent.NotifyStateChanged();

            logger.LogInformation("Sub-agent final summary received.");

            return summaryMessage.Content.Length > 0
                ? summaryMessage.Content
                : MaxIterationsReport(session, excludeMessage: summaryMessage);
        }
        catch (OperationCanceledException)
        {
            summaryMessage.IsStreaming = false;
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sub-agent final summary call failed. Falling back to last response.");
            subAgent.RemoveMessage(summaryMessage);
            subAgent.RemoveMessage(requestMessage);
            return MaxIterationsReport(session);
        }
    }

    /// <summary>Returns a report when the execution time or token budget is exceeded, otherwise null.</summary>
    private string? GetLimitExceededMessage(ConversationSession session, long startedAt)
    {
        var profile = profileManager.ActiveProfile;

        if (profile.MaxExecutionTimePerSubAgent > 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var limit = TimeSpan.FromSeconds(profile.MaxExecutionTimePerSubAgent);
            if (elapsed > limit)
                return LimitMessage($"execution time limit ({elapsed.TotalMinutes:F1} / {limit.TotalMinutes:F0} minutes)", session);
        }

        if (profile.MaxTokensPerSubAgent > 0 && session.TotalTokens > profile.MaxTokensPerSubAgent)
            return LimitMessage($"token budget ({session.TotalTokens} / {profile.MaxTokensPerSubAgent} tokens)", session);

        return null;
    }

    private string LimitMessage(string limit, ConversationSession session)
    {
        logger.LogInformation("Sub-agent exceeded {Limit}.", limit);
        return $"Sub-agent exceeded {limit}. Last response: {LastAssistantContent(session)}";
    }

    private static string LastAssistantContent(ConversationSession session)
        => session.GetLastOrDefaultMessage(m => m.Role == ChatMessageRole.Assistant)?.Content is { Length: > 0 } content
            ? content
            : "(no response)";

    private string MaxIterationsReport(ConversationSession session, VisualChatMessage? excludeMessage = null)
        => $"Sub-agent reached the maximum number of iterations ({profileManager.ActiveProfile.MaxIterationsPerSubAgent}) without completing. " +
           "Last response: " + session.GetLastOrDefaultMessage(m => m.Role == ChatMessageRole.Assistant && m != excludeMessage)?.Content;

    /// <summary>Sets the terminal state and notifies subscribers. Failures stay expanded for inspection.</summary>
    private void Finish(SubAgentMessage subAgent, SubAgentStatus status, string? errorMessage = null)
    {
        subAgent.Status = status;
        subAgent.CompletedAt = DateTime.Now;
        subAgent.ErrorMessage = errorMessage;
        if (status != SubAgentStatus.Failed)
            subAgent.IsExpanded = false;

        subAgent.NotifyStateChanged();
        NotifyStructuralChange(subAgent);
    }

    private void NotifyStructuralChange(SubAgentMessage subAgent)
        => Volatile.Read(ref SubAgentStateChanged)?.Invoke(subAgent);

    private void OnApprovalRequired(SubAgentMessage subAgent, string toolCallId)
    {
        subAgent.PendingToolCallId = toolCallId;
        subAgent.IsExpanded = true;
        subAgent.NotifyStateChanged();
        NotifyStructuralChange(subAgent);
    }

    private void SyncTokens(SubAgentMessage subAgent, ConversationSession session)
        => subAgent.TotalTokens = session.TotalTokens;

    private static VisualChatMessage AddUserMessage(SubAgentMessage subAgent, string content)
    {
        var message = new VisualChatMessage
        {
            Content = content,
            Role = ChatMessageRole.User,
            IsExpanded = true
        };
        subAgent.AddMessage(message);
        subAgent.NotifyStateChanged();
        return message;
    }

    /// <summary>
    /// Network failures, timeouts and API errors are transient. An OperationCanceledException
    /// that is NOT caused by our token (e.g. HttpClient timeout surfacing as
    /// TaskCanceledException) is treated as transient too — the cancellation-specific catch
    /// above only matches when <c>cancellationToken.IsCancellationRequested</c>.
    /// </summary>
    private static bool IsTransientError(Exception ex)
        => ex is HttpRequestException or TimeoutException or LlmApiException or IOException
            or OperationCanceledException;

    private static string GetArg(IReadOnlyDictionary<string, object> args, string key)
        => args.TryGetValue(key, out var value) ? value?.ToString()?.Trim() ?? string.Empty : string.Empty;

    /// <summary>Parses an optional string-array argument (accepts JSON arrays and object lists).</summary>
    private static string[]? ParseStringArray(IReadOnlyDictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        var items = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } json => json.EnumerateArray().Select(e => e.GetString()),
            IList list => list.OfType<object>().Select(o => o?.ToString()),
            _ => null
        };

        if (items is null)
            return null;

        var result = items
            .Select(s => s?.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .OfType<string>()
            .ToArray();

        return result.Length > 0 ? result : null;
    }

    /// <summary>
    /// Filters Agent-mode tools: always drops SubAgent (no recursion) and ModeSwitch categories;
    /// with a non-empty whitelist everything outside it is dropped as well.
    /// </summary>
    private IEnumerable<Tool> BuildSubAgentTools(string[]? allowedTools)
    {
        foreach (var tool in toolManager.GetEnabledTools(AppMode.Agent))
        {
            if (ExcludedCategories.Contains(tool.Category))
                continue;

            if (allowedTools is { Length: > 0 } && !allowedTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            yield return tool;
        }
    }
}
