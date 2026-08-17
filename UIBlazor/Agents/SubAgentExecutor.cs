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
    ILogger<SubAgentExecutor> logger) : ISubAgentExecutor
{
    // TODO: Вынести в CommonOptions или ConnectionProfile
    private const int MaxIterations = 20;

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

        // Parse allowed/denied tools
        var allowedTools = ParseStringArray(args, "allowedTools");
        var deniedTools = ParseStringArray(args, "deniedTools");

        // Build filtered tool set for the sub-agent
        var subAgentTools = BuildSubAgentTools(allowedTools, deniedTools);

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
            DeniedTools = deniedTools,
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
        };

        toolCall.SubAgent = subAgent;

        // Wire sub-agent state changes to the executor-level event so AiChat
        // can trigger Blazor re-rendering during sub-agent execution.
        // Without this, the ToolCallBlock component never re-renders during
        // sub-agent work because it hasn't subscribed yet (SubAgent was null
        // when OnParametersSet first ran).
        subAgent.StateChanged += () =>
        {
            SubAgentStateChanged?.Invoke(subAgent);
        };

        // Initial notification so AiChat re-renders and ToolCallBlock subscribes
        SubAgentStateChanged?.Invoke(subAgent);

        // Create a temporary session for the sub-agent (not saved to localStorage)
        var session = new ConversationSession
        {
            Id = $"subagent_{DateTime.Now:s}",
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

            logger.LogError(ex, "Sub-agent failed.");

            return new VsToolResult
            {
                Name = BuiltInToolEnum.DelegateTask,
                Success = false,
                ErrorMessage = $"Sub-agent failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Main execution loop: stream LLM response → process tool calls → repeat until done.
    /// Uses CompletionsResult to capture LLM state instead of reading shared ChatService properties.
    /// Uses a dedicated ToolCallHandler to isolate approval waiters from the main agent.
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
            await chatService.ProcessStreamAsync(
                assistantMessage,
                chatService.GetCompletionsForSubAgentAsync(session, systemPrompt, subAgentTools, resultCapture, cancellationToken),
                onContentUpdate: _ =>
                {
                    assistantMessage.IsShouldRender = true;
                    subAgent.NotifyStateChanged();
                },
                onToolCallsUpdate: toolCalls =>
                {
                    assistantMessage.ToolCalls = toolCalls;
                    assistantMessage.IsShouldRender = true;
                    subAgent.NotifyStateChanged();
                },
                onStateChange: () =>
                {
                    assistantMessage.Model ??= resultCapture.Model;
                    subAgent.NotifyStateChanged();
                },
                resultCapture,
                cancellationToken);

            // Read captured state (not shared ChatService properties)
            assistantMessage.ToolCalls = resultCapture.AccumulatedToolCalls;
            assistantMessage.IsStreaming = false;
            subAgent.NotifyStateChanged();

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

                session.TotalTokens += assistantMessage.ToolCalls?.Sum(t => t.Tokens) ?? 0;
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
    /// Applies allowedTools (whitelist) and deniedTools (blacklist) if specified.
    /// </summary>
    private IEnumerable<Tool> BuildSubAgentTools(string[]? allowedTools, string[]? deniedTools)
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

            // If deniedTools is specified, exclude tools in the blacklist
            if (deniedTools is { Length: > 0 } && deniedTools.Contains(tool.Name))
                continue;

            yield return tool;
        }
    }
}
