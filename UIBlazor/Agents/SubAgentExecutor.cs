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
    ILogger<SubAgentExecutor> logger) : ISubAgentExecutor
{
    // TODO: Вынести в CommonOptions или ConnectionProfile
    private const int MaxIterations = 20;

    // TODO: Token budget for sub-agents.
    // Currently a sub-agent can consume unlimited tokens (up to MaxIterations × max response size).
    // To implement: add a MaxTokens option (e.g. in ConnectionProfile or as a delegate_task parameter),
    // then check session.TotalTokens against it at the top of the loop in RunSubAgentLoopAsync.
    // If exceeded — break and return a "token budget exceeded" message.
    // This prevents unexpected API costs from runaway sub-agent loops.

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
        subAgent.Messages.Add(userMessage);
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
        catch (OperationCanceledException)
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
        subAgent.Messages.Add(compressMessage);
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

            logger.LogInformation("Sub-agent context compression completed. Tokens after: {Tokens}", session.TotalTokens);
        }
        catch (OperationCanceledException)
        {
            // Compression cancelled — remove the compression message and let the outer handler deal with cancellation
            session.Messages.Remove(compressMessage);
            subAgent.Messages.Remove(compressMessage);
            throw;
        }
        catch (Exception ex)
        {
            // Compression failed — remove the compression message and continue without compression
            logger.LogWarning(ex, "Sub-agent context compression failed. Continuing with current context.");
            session.Messages.Remove(compressMessage);
            subAgent.Messages.Remove(compressMessage);
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

        while (iteration < MaxIterations && !cancellationToken.IsCancellationRequested)
        {
            iteration++;

            // Check if context compression is needed before the next LLM call
            if (NeedCompression(session))
            {
                await CompressSubAgentSessionAsync(session, subAgent, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            // Create assistant message for streaming
            var assistantMessage = new VisualChatMessage
            {
                Role = ChatMessageRole.Assistant,
                IsStreaming = true,
                IsExpanded = true
            };
            session.AddMessage(assistantMessage);
            subAgent.Messages.Add(assistantMessage);
            subAgent.NotifyStateChanged();

            // Capture LLM state in a dedicated CompletionsResult (not shared with main agent)
            var resultCapture = new CompletionsResult();

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

        if (iteration >= MaxIterations)
        {
            return $"Sub-agent reached the maximum number of iterations ({MaxIterations}) without completing. " +
                   "Last response: " + session.Messages.LastOrDefault(m => m.Role == ChatMessageRole.Assistant)?.Content;
        }

        return "Sub-agent was cancelled.";
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
            result = el.EnumerateArray()
                .Select(e => e.GetString()?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
        }
        else if (obj is IList list)
        {
            result = list.OfType<object>()
                .Select(o => o?.ToString()?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
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
