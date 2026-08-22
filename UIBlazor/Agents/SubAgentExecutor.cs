namespace UIBlazor.Agents;

/// <summary>
/// Executes sub-agent tasks delegated by the main agent via the delegate_task tool.
/// The sub-agent runs with its own system prompt, conversation context, and filtered tool set.
/// Sub-agents cannot delegate further (delegate_task is always excluded from their tools).
/// Sub-agents cannot switch mode or make plans (switch_mode is always excluded).
/// Sub-agents always run in Agent mode.
/// </summary>
public class SubAgentExecutor(
    IChatService chatService,
    IToolManager toolManager,
    ISystemPromptBuilder systemPromptBuilder,
    IProfileManager profileManager,
    IRetryHandler retryHandler,
    ILogger<SubAgentExecutor> logger) : ISubAgentExecutor
{
    /// <summary>
    /// Maximum number of retry attempts for transient LLM API/network errors.
    /// Total attempts = 1 original + MaxRetries.
    /// </summary>
    private const int MaxRetries = 2;

    /// <inheritdoc />
    public event Action<SubAgentMessage>? SubAgentStateChanged;

    /// <summary>
    /// Tool categories that are always excluded from sub-agent tools.
    /// - SubAgent: prevents recursion (sub-agent cannot delegate further)
    /// - ModeSwitch: prevents sub-agent from switching mode or making plans
    /// </summary>
    private static readonly HashSet<ToolCategory> ExcludedCategories =
    [
        ToolCategory.SubAgent,
        ToolCategory.ModeSwitch,
    ];

    public async Task<VsToolResult> ExecuteAsync(
        string argsJson,
        ToolCall toolCall,
        CancellationToken cancellationToken)
    {
        // Parse arguments (returns empty dict on failure, never null)
        var args = JsonUtils.DeserializeParameters(argsJson ?? "{}");

        var task = args.TryGetValue("task", out var taskObj) ? taskObj?.ToString()?.Trim() ?? string.Empty : string.Empty;
        var systemPrompt = args.TryGetValue("systemPrompt", out var promptObj) ? promptObj?.ToString()?.Trim() ?? string.Empty : string.Empty;

        if (string.IsNullOrEmpty(task))
        {
            return new VsToolResult
            {
                Name = BuiltInToolEnum.DelegateTask,
                Success = false,
                ErrorMessage = "delegate_task requires a 'task' parameter."
            };
        }

        // Build the full system prompt: combine the LLM-provided custom prompt with
        // the same context sections as the main agent (rules, skills, solution structure, etc.).
        // Exceptions: no active file content, no Mermaid instructions.
        var fullSystemPrompt = await systemPromptBuilder.PrepareSubAgentSystemPromptAsync(
            systemPrompt, cancellationToken);

        // Parse allowed tools
        var allowedTools = ParseStringArray(args, "allowedTools");

        // Build filtered tool set for the sub-agent
        var subAgentTools = BuildSubAgentTools(allowedTools);

        // Create a dedicated ToolCallHandler for the sub-agent.
        // This isolates approval waiters from the main agent's ToolCallHandler,
        // preventing CancelPendingApprovals from cancelling main agent's pending approvals.
        var subAgentToolCallHandler = new ToolCallHandler(toolManager);

        // Create sub-agent data model and attach to the specific tool call
        var subAgent = new SubAgentMessage
        {
            Task = task,
            SystemPrompt = systemPrompt,
            AllowedTools = allowedTools,
            Status = SubAgentStatus.Running,
            StartedAt = DateTime.Now,
            IsExpanded = true, // Auto-expand while running so user can see progress
            ToolCallHandler = subAgentToolCallHandler, // Sub-agent's own handler for UI approval routing
        };

        // Wire the sub-agent handler's ApprovalRequired event to set PendingToolCallId
        // on the SubAgentMessage and trigger state change notification.
        // AiChat subscribes to SubAgentStateChanged and will check PendingToolCallId
        // to show notification + scroll to the tool.
        subAgentToolCallHandler.ApprovalRequired += toolCallId =>
        {
            subAgent.PendingToolCallId = toolCallId;
            // Ensure sub-agent is expanded so the user can see the tool requiring approval
            subAgent.IsExpanded = true;
            subAgent.NotifyStateChanged();
            // Explicitly notify AiChat — this is a structural event that needs user attention
            Volatile.Read(ref SubAgentStateChanged)?.Invoke(subAgent);
        };

        toolCall.SubAgent = subAgent;

        // NOTE: We intentionally do NOT relay subAgent.StateChanged to SubAgentStateChanged.
        // StateChanged fires on every streaming token (high frequency), and relaying it
        // would cause AiChat to re-render the ENTIRE chat tree on every token.
        // Instead, SubAgentStateChanged is invoked explicitly only for structural events:
        // - Initial notification (below)
        // - Approval required (in ApprovalRequired handler above)
        // - Status changes: completed/cancelled/failed (in try/catch below)
        // SubAgentView handles its own throttled re-rendering for content updates.

        // Initial notification so AiChat re-renders and ToolCallBlock subscribes
        Volatile.Read(ref SubAgentStateChanged)?.Invoke(subAgent);

        // Create a temporary session for the sub-agent (not saved to localStorage)
        // GUID suffix ensures uniqueness even when multiple sub-agents start in the same second
        var session = new ConversationSession
        {
            Id = $"subagent_{DateTime.Now:s}_{Guid.NewGuid():N}",
            Mode = AppMode.Agent // Sub-agent is always in Agent mode
        };

        // Add the task as the initial user message
        var userMessage = new VisualChatMessage
        {
            Content = task,
            Role = ChatMessageRole.User,
            IsExpanded = true
        };
        session.AddMessage(userMessage);
        subAgent.AddMessage(userMessage);
        subAgent.NotifyStateChanged();

        logger.LogInformation("Sub-agent started. Task: {Task}", task);

        try
        {
            var result = await RunSubAgentLoopAsync(session, subAgent, fullSystemPrompt, subAgentTools, subAgentToolCallHandler, cancellationToken);

            subAgent.Status = SubAgentStatus.Completed;
            subAgent.CompletedAt = DateTime.Now;
            subAgent.Result = result;
            subAgent.TotalTokens = session.TotalTokens;
            subAgent.IsExpanded = false; // Collapse when done
            subAgent.NotifyStateChanged();
            // Notify AiChat that sub-agent is done (structural change)
            Volatile.Read(ref SubAgentStateChanged)?.Invoke(subAgent);

            logger.LogInformation("Sub-agent completed. Tokens: {Tokens}", session.TotalTokens);

            return new VsToolResult
            {
                Name = BuiltInToolEnum.DelegateTask,
                Success = true,
                Result = result
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            subAgent.Status = SubAgentStatus.Cancelled;
            subAgent.CompletedAt = DateTime.Now;
            subAgent.ErrorMessage = "Cancelled by user.";
            subAgent.IsExpanded = false; // Collapse when done
            subAgent.NotifyStateChanged();
            // Notify AiChat that sub-agent was cancelled (structural change)
            Volatile.Read(ref SubAgentStateChanged)?.Invoke(subAgent);

            logger.LogInformation("Sub-agent cancelled.");

            return VsToolResult.Cancelled(BuiltInToolEnum.DelegateTask);
        }
        catch (Exception ex)
        {
            subAgent.Status = SubAgentStatus.Failed;
            subAgent.CompletedAt = DateTime.Now;
            subAgent.ErrorMessage = ex.Message;
            subAgent.IsExpanded = false; // Collapse when done
            subAgent.NotifyStateChanged();
            // Notify AiChat that sub-agent failed (structural change)
            Volatile.Read(ref SubAgentStateChanged)?.Invoke(subAgent);

            logger.LogError(ex, "Sub-agent failed.");

            return new VsToolResult
            {
                Name = BuiltInToolEnum.DelegateTask,
                Success = false,
                ErrorMessage = $"Sub-agent failed: {ex.Message}"
            };
        }
        finally
        {
            // Clean up any pending approval waiters on the sub-agent's handler.
            // This prevents dangling TCSes if the sub-agent exits mid-approval.
            subAgentToolCallHandler.CancelPendingApprovals();

            // Release heavy runtime resources that are no longer needed after
            // the sub-agent has finished. This clears ToolCallHandler (all waiters
            // already cancelled above), Segments in messages (not used by SubAgentView),
            // and transient flags. The full Messages list with Content/ReasoningContent/
            // ToolCalls is preserved so the user can still expand and review the
            // sub-agent's reasoning chain in the UI.
            subAgent.ReleaseMemory();
        }
    }

    /// <summary>
    /// Checks if the sub-agent session needs context compression.
    /// Uses the same TokensToCompress threshold as the main agent.
    /// </summary>
    private bool NeedCompression(ConversationSession session)
        => profileManager.ActiveProfile.TokensToCompress > 0
           && session.TotalTokens > profileManager.ActiveProfile.TokensToCompress;

    /// <summary>
    /// Compresses the sub-agent session context.
    /// No retries — if compression fails, the sub-agent continues with the current context.
    /// Updates subAgent.TotalTokens and notifies UI.
    /// </summary>
    private async Task CompressSubAgentSessionAsync(
        ConversationSession session,
        SubAgentMessage subAgent,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Sub-agent context compression started. Tokens: {Tokens}", session.TotalTokens);

        subAgent.IsCompressing = true;
        subAgent.NotifyStateChanged();

        var compressResult = new CompletionsResult();
        var compressMessage = new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            IsStreaming = true,
            IsExpanded = true,
            Content = "## ♻ Context compression...\n\n"
        };
        session.AddMessage(compressMessage);
        subAgent.AddMessage(compressMessage);
        subAgent.NotifyStateChanged();

        try
        {
            await chatService.ProcessStreamAsync(
                compressMessage,
                chatService.CompressSessionAsync(session, compressResult, cancellationToken),
                onContentUpdate: _ =>
                {
                    compressMessage.IsShouldRender = true;
                    subAgent.TotalTokens = session.TotalTokens;
                    subAgent.NotifyStateChanged();
                },
                onToolCallsUpdate: _ => { },
                onStateChange: () =>
                {
                    compressMessage.Model ??= compressResult.Model;
                    subAgent.TotalTokens = session.TotalTokens;
                    subAgent.NotifyStateChanged();
                },
                compressResult,
                cancellationToken);

            // CompressSessionAsync replaces session.Messages with a new list (keptMessages).
            // subAgent.Messages still holds the old uncompressed history + compressMessage.
            // Synchronize subAgent.Messages with the compressed session.Messages so the UI
            // displays the correct (compressed) conversation history.
            subAgent.Messages = new List<VisualChatMessage>(session.Messages);

            logger.LogInformation("Sub-agent context compression completed. Tokens after: {Tokens}", session.TotalTokens);
        }
        catch (OperationCanceledException)
        {
            // Compression cancelled — remove the compression message and let the outer handler deal with cancellation
            session.Messages.Remove(compressMessage);
            subAgent.RemoveMessage(compressMessage);
            throw;
        }
        catch (Exception ex)
        {
            // Compression failed — remove the compression message and continue without compression
            logger.LogWarning(ex, "Sub-agent context compression failed. Continuing with current context.");
            session.Messages.Remove(compressMessage);
            subAgent.RemoveMessage(compressMessage);
        }
        finally
        {
            compressMessage.IsStreaming = false;
            subAgent.IsCompressing = false;
            subAgent.TotalTokens = session.TotalTokens;
            subAgent.NotifyStateChanged();
        }
    }

    /// <summary>
    /// Main execution loop: stream LLM response → process tool calls → repeat until done.
    /// Uses CompletionsResult to capture LLM state instead of reading shared ChatService properties.
    /// Uses a dedicated ToolCallHandler to isolate approval waiters from the main agent.
    /// Includes dynamic token counting (updates subAgent.TotalTokens during streaming)
    /// and automatic context compression when token threshold is exceeded.
    /// </summary>
    private async Task<string> RunSubAgentLoopAsync(
        ConversationSession session,
        SubAgentMessage subAgent,
        string systemPrompt,
        IEnumerable<Tool> subAgentTools,
        IToolCallHandler toolCallHandler,
        CancellationToken cancellationToken)
    {
        var iteration = 0;
        var profile = profileManager.ActiveProfile;
        var maxTokens = profile.MaxTokensPerSubAgent;
        var maxIterations = profile.MaxIterationsPerSubAgent > 0
            ? profile.MaxIterationsPerSubAgent
            : int.MaxValue; // If MaxIterationsPerSubAgent <= 0, the limit is disabled.

        while (iteration < maxIterations && !cancellationToken.IsCancellationRequested)
        {
            // Check token budget before starting a new iteration.
            // If MaxTokensPerSubAgent <= 0, the limit is disabled.
            if (maxTokens > 0 && session.TotalTokens > maxTokens)
            {
                var lastContent = session.Messages
                    .LastOrDefault(m => m.Role == ChatMessageRole.Assistant)?.Content ?? "(no response)";

                logger.LogInformation(
                    "Sub-agent exceeded token budget: {Tokens} / {MaxTokens} tokens.",
                    session.TotalTokens, maxTokens);

                return $"Sub-agent exceeded token budget ({session.TotalTokens} / {maxTokens} tokens). " +
                       $"Last response: {lastContent}";
            }

            iteration++;

            // Check if context compression is needed before the next LLM call
            if (NeedCompression(session))
            {
                await CompressSubAgentSessionAsync(session, subAgent, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            // --- LLM call with retry logic for transient errors ---
            // Up to MaxRetries+1 attempts (1 original + MaxRetries retries).
            // Only HttpRequestException, TimeoutException, and API-level errors (resultCapture.Error) are retried.
            // OperationCanceledException is never retried — it propagates immediately.
            VisualChatMessage? assistantMessage = null;
            CompletionsResult? resultCapture = null;

            // Snapshot TotalTokens before the first attempt.
            // ChatService.GetCompletionsAsync increments session.TotalTokens during streaming
            // (either dynamically per-chunk or via usage data in the final chunk).
            // On retry we must roll back to this snapshot so that tokens from the failed
            // attempt are not counted — session.Messages.Remove() alone does NOT update TotalTokens.
            var tokensBeforeAttempt = session.TotalTokens;

            for (var attempt = 0; ; attempt++)
            {
                // Create a fresh assistant message and result capture for each attempt.
                // Previous (partially filled) message must be removed from session and sub-agent.
                if (assistantMessage is not null)
                {
                    // Roll back TotalTokens to the snapshot taken before the first attempt.
                    // This correctly handles both dynamic per-chunk counting and usage-based updates.
                    session.TotalTokens = tokensBeforeAttempt;
                    session.Messages.Remove(assistantMessage);
                    subAgent.RemoveMessage(assistantMessage);
                }

                assistantMessage = new VisualChatMessage
                {
                    Role = ChatMessageRole.Assistant,
                    IsStreaming = true,
                    IsExpanded = true
                };
                session.AddMessage(assistantMessage);
                subAgent.AddMessage(assistantMessage);
                subAgent.NotifyStateChanged();

                // Capture LLM state in a dedicated CompletionsResult (not shared with main agent)
                resultCapture = new CompletionsResult();

                try
                {
                    // Stream the LLM response.
                    // ProcessStreamAsync already accumulates content into assistantMessage.Content
                    // (via internal StringBuilder) and calls onContentUpdate with the DELTA (single token).
                    // We must NOT overwrite assistantMessage.Content — just trigger UI re-render.
                    // Dynamic token counter: update subAgent.TotalTokens from session.TotalTokens on every chunk.
                    await chatService.ProcessStreamAsync(
                        assistantMessage,
                        chatService.GetCompletionsForSubAgentAsync(session, systemPrompt, subAgentTools, resultCapture, cancellationToken),
                        onContentUpdate: _ =>
                        {
                            assistantMessage.IsShouldRender = true;
                            subAgent.TotalTokens = session.TotalTokens;
                            subAgent.NotifyStateChanged();
                        },
                        onToolCallsUpdate: toolCalls =>
                        {
                            assistantMessage.ToolCalls = toolCalls;
                            assistantMessage.IsShouldRender = true;
                            subAgent.TotalTokens = session.TotalTokens;
                            subAgent.NotifyStateChanged();
                        },
                        onStateChange: () =>
                        {
                            // Model is set once, then this is a no-op.
                            assistantMessage.Model ??= resultCapture.Model;
                            subAgent.TotalTokens = session.TotalTokens;
                        },
                        resultCapture,
                        cancellationToken);

                    // Check for API-level errors captured during streaming
                    if (!string.IsNullOrEmpty(resultCapture.Error))
                    {
                        throw new Exception($"LLM API error: {resultCapture.Error}");
                    }

                    // Success — break out of the retry loop
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Cancellation must never be retried — propagate immediately
                    assistantMessage.IsStreaming = false;
                    throw;
                }
                catch (Exception ex) when (IsTransientError(ex) && attempt < MaxRetries)
                {
                    // Transient error (HttpRequestException, TimeoutException, or API error wrapper) — retry
                    var delaySeconds = retryHandler.GetRetryDelay(attempt + 1);
                    logger.LogWarning(ex,
                        "Sub-agent LLM call failed (attempt {Attempt}/{Total}). Retrying in {Delay}s.",
                        attempt + 1, MaxRetries + 1, delaySeconds);

                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                    // Loop continues — new assistantMessage and resultCapture will be created
                }
            }

            // Read captured state (not shared ChatService properties)
            assistantMessage.ToolCalls = resultCapture.AccumulatedToolCalls;
            assistantMessage.IsStreaming = false;
            // Update token counter after streaming completes (usage data may have arrived in final chunk)
            subAgent.TotalTokens = session.TotalTokens;
            subAgent.NotifyStateChanged();

            // Check cancellation before processing tool calls
            if (cancellationToken.IsCancellationRequested)
                break;

            if (assistantMessage.ToolCalls is { Count: > 0 })
            {
                // Process tool calls sequentially (with approval flow)
                // Uses the sub-agent's own ToolCallHandler, not the main agent's
                toolCallHandler.PrepareToolsForApprovals(assistantMessage.ToolCalls);
                assistantMessage.IsShouldRender = true;
                subAgent.NotifyStateChanged();

                await toolCallHandler.ProcessToolCallsAsync(
                    assistantMessage.ToolCalls,
                    cancellationToken);

                // Note: session.TotalTokens is already updated by ChatService.GetCompletionsAsync
                // from the API's usage data. Do NOT add tool call token estimates here —
                // that would double-count tokens.
                subAgent.TotalTokens = session.TotalTokens;
                subAgent.NotifyStateChanged();

                // Continue the loop to get the next LLM response
                continue;
            }

            // No tool calls — the sub-agent is done
            // Return the final content (strip thinking blocks if any)
            var content = assistantMessage.Content;
            if (string.IsNullOrEmpty(content))
            {
                content = "(Sub-agent returned an empty response.)";
            }

            return content;
        }

        if (iteration >= maxIterations)
        {
            // The sub-agent exhausted all iterations without finishing.
            // Give it one final chance to produce a meaningful summary instead of
            // returning the raw last response.
            return await RequestFinalSummaryAsync(session, subAgent, systemPrompt, subAgentTools, cancellationToken);
        }

        throw new OperationCanceledException("Sub-agent was cancelled.");
    }

    /// <summary>
    /// Performs a final LLM call asking the sub-agent to summarize its work when the
    /// iteration limit has been reached. This gives the main agent a meaningful summary
    /// instead of the raw last response.
    /// No tool calls are expected — the LLM should respond with text only.
    /// No retry logic: if this call fails, the raw last response is returned as fallback.
    /// </summary>
    private async Task<string> RequestFinalSummaryAsync(
        ConversationSession session,
        SubAgentMessage subAgent,
        string systemPrompt,
        IEnumerable<Tool> subAgentTools,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Sub-agent reached max iterations ({MaxIterations}). Requesting final summary.",
            profileManager.ActiveProfile.MaxIterationsPerSubAgent);

        const string summaryInstruction =
            "You are about to reach your iteration limit. " +
            "Provide a comprehensive summary of what you have accomplished so far, " +
            "what remains to be done, and any important findings or decisions. " +
            "This is your final response.";

        // Add the summary instruction as a user message
        var summaryRequestMessage = new VisualChatMessage
        {
            Content = summaryInstruction,
            Role = ChatMessageRole.User,
            IsExpanded = true
        };
        session.AddMessage(summaryRequestMessage);
        subAgent.AddMessage(summaryRequestMessage);
        subAgent.NotifyStateChanged();

        // Create assistant message for the summary response
        var summaryMessage = new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            IsStreaming = true,
            IsExpanded = true
        };
        session.AddMessage(summaryMessage);
        subAgent.AddMessage(summaryMessage);
        subAgent.NotifyStateChanged();

        var resultCapture = new CompletionsResult();

        try
        {
            await chatService.ProcessStreamAsync(
                summaryMessage,
                chatService.GetCompletionsForSubAgentAsync(session, systemPrompt, [], resultCapture, cancellationToken),
                onContentUpdate: _ =>
                {
                    summaryMessage.IsShouldRender = true;
                    subAgent.TotalTokens = session.TotalTokens;
                    subAgent.NotifyStateChanged();
                },
                onToolCallsUpdate: toolCalls =>
                {
                    summaryMessage.ToolCalls = toolCalls;
                    summaryMessage.IsShouldRender = true;
                    subAgent.TotalTokens = session.TotalTokens;
                    subAgent.NotifyStateChanged();
                },
                onStateChange: () =>
                {
                    summaryMessage.Model ??= resultCapture.Model;
                    subAgent.TotalTokens = session.TotalTokens;
                },
                resultCapture,
                cancellationToken);

            summaryMessage.ToolCalls = resultCapture.AccumulatedToolCalls;
            summaryMessage.IsStreaming = false;
            subAgent.TotalTokens = session.TotalTokens;
            subAgent.NotifyStateChanged();

            var content = summaryMessage.Content;
            if (string.IsNullOrEmpty(content))
            {
                // Fallback: LLM returned empty content
                content = $"Sub-agent reached the maximum number of iterations ({profileManager.ActiveProfile.MaxIterationsPerSubAgent}) without completing. " +
                          "Last response: " + session.Messages
                              .LastOrDefault(m => m.Role == ChatMessageRole.Assistant && m != summaryMessage)?.Content;
            }

            logger.LogInformation("Sub-agent final summary received.");
            return content;
        }
        catch (OperationCanceledException)
        {
            // Cancellation — clean up and rethrow so the outer handler sets Cancelled status
            summaryMessage.IsStreaming = false;
            throw;
        }
        catch (Exception ex)
        {
            // Summary call failed — fall back to the raw last response
            logger.LogWarning(ex, "Sub-agent final summary call failed. Falling back to last response.");

            // Remove the summary messages so they don't clutter the UI
            session.Messages.Remove(summaryMessage);
            subAgent.RemoveMessage(summaryMessage);
            session.Messages.Remove(summaryRequestMessage);
            subAgent.RemoveMessage(summaryRequestMessage);

            return $"Sub-agent reached the maximum number of iterations ({profileManager.ActiveProfile.MaxIterationsPerSubAgent}) without completing. " +
                   "Last response: " + session.Messages.LastOrDefault(m => m.Role == ChatMessageRole.Assistant)?.Content;
        }
    }

    /// <summary>
    /// Determines whether an exception represents a transient (retryable) error.
    /// Retried: HttpRequestException (network/API), TimeoutException, and Exception wrapping an API error.
    /// NOT retried: OperationCanceledException (handled separately), non-transient exceptions.
    /// </summary>
    private static bool IsTransientError(Exception ex)
    {
        // HttpRequestException covers network failures and HTTP 5xx responses
        if (ex is HttpRequestException)
            return true;

        // TimeoutException covers request timeouts
        if (ex is TimeoutException)
            return true;

        // API-level errors are thrown as generic Exception with "LLM API error:" prefix
        // (from the resultCapture.Error check above)
        if (ex is not null && ex.Message.StartsWith("LLM API error:", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Parses a string array parameter from the args dictionary.
    /// Handles both JsonElement (from ToolCore.JsonUtils) and List<object> (from UIBlazor.Utils.JsonUtils).
    /// Returns null if the parameter is missing or empty.
    /// </summary>
    private static string[]? ParseStringArray(IReadOnlyDictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var obj) || obj is null)
            return null;

        string[]? result = null;

        if (obj is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            result = [.. el.EnumerateArray()
                .Select(e => e.GetString()?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))];
        }
        else if (obj is IList list)
        {
            result = [.. list.OfType<object>()
                .Select(o => o?.ToString()?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))];
        }

        return result is { Length: > 0 } ? result : null;
    }

    /// <summary>
    /// Builds the filtered tool set for the sub-agent.
    /// Excludes tools by category (SubAgent, ModeSwitch) and by name (delegate_task, switch_mode).
    /// If allowedTools is specified, only those tools are included; all others are denied.
    /// If allowedTools is null/empty, all tools are available.
    /// </summary>
    private IEnumerable<Tool> BuildSubAgentTools(string[]? allowedTools)
    {
        var allTools = toolManager.GetEnabledTools(AppMode.Agent);

        foreach (var tool in allTools)
        {
            // Exclude by category: no recursion (SubAgent), no mode switching (ModeSwitch)
            if (ExcludedCategories.Contains(tool.Category))
                continue;

            // If allowedTools is specified, only include tools in the whitelist
            if (allowedTools is { Length: > 0 } && !allowedTools.Contains(tool.Name))
                continue;

            yield return tool;
        }
    }
}
