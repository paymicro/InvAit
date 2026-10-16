namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Sub-agents run as parallel branches (Task.WhenAll fan-out).
/// One delegate_task call carries a list of tasks; each task becomes a branch with a
/// full tool-invocation loop via <see cref="AfAgentLoop"/>, all branches run concurrently
/// and the call returns only after every branch has finished. Nested delegation (recursion)
/// works the same way: a branch's toolset includes delegate_task when depth &lt; MaxSubAgentDepth,
/// and the nested call builds its own fan-out inside the branch's tool execution.
/// Streaming/approvals per branch are routed into its own <see cref="SubAgentMessage"/>
/// and isolated <see cref="IToolCallHandler"/>, so the UI flow (SubAgentView, approval
/// toolbar, PendingToolCallId notifications) stays identical to the legacy executor.
/// </summary>
public sealed class AfSubAgentOrchestrator(
    IProfileManager profileManager,
    ISystemPromptBuilder systemPromptBuilder,
    IToolManager toolManager,
    IChatService chatService,
    IContentFilter contentFilter,
    HttpClient httpClient,
    ICommonSettingsProvider commonSettingsProvider,
    IRetryHandler retryHandler,
    ILogger<AfSubAgentOrchestrator> logger) : ISubAgentExecutor
{
    private const string EmptyResponse = "(Sub-agent returned an empty response.)";
    private const string MissingTasksError =
        "delegate_task requires a non-empty 'tasks' array (each item: task, systemPrompt, allowedTools).";

    /// <summary>Only mode switching may never be delegated to sub-agents; delegate_task itself
    /// is excluded from GetEnabledTools output and replaced by the depth-aware tool below.</summary>
    private static readonly HashSet<ToolCategory> ExcludedCategories = [ToolCategory.ModeSwitch];

    /// <summary>
    /// Trailing-throttle window for per-token sub-agent UI events (~10/sec). Matches the
    /// SubAgentView render interval: throttling the source below the subscriber's window
    /// keeps the render pump idle between trailing fires.
    /// </summary>
    private const int SubAgentUiIntervalMs = 100;

    public event Action<SubAgentMessage>? SubAgentStateChanged;

    public Task<VsToolResult> ExecuteAsync(string argsJson, ToolCall toolCall, CancellationToken cancellationToken)
        => RunBatchAsync(argsJson, toolCall, depth: 0, ownerSubAgent: null, cancellationToken);

    /// <summary>
    /// Runs a delegation batch at the given nesting depth (0 = called by the main agent).
    /// Branches are executed concurrently and the call returns only after all complete.
    /// </summary>
    private async Task<VsToolResult> RunBatchAsync(
        string argsJson, ToolCall toolCall, int depth, SubAgentMessage? ownerSubAgent, CancellationToken cancellationToken)
    {
        var tasks = ParseTasks(argsJson);
        if (tasks.Count == 0)
            return VsToolResult.Failed(BuiltInToolEnum.DelegateTask, MissingTasksError);

        // Build the UI state first: SubAgentView appears as soon as the call is executing.
        var tickets = CreateTickets(tasks, toolCall, depth, ownerSubAgent);

        try
        {
            // RunBranchAsync reports every in-branch failure itself (as failure text or a
            // graceful timeout message) and rethrows only on chat-level cancellation,
            // so any exception escaping Task.WhenAll means the root token fired.
            await Task.WhenAll(tickets.Select(t => RunBranchAsync(t, cancellationToken))).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Delegation batch cancelled by user.");
            foreach (var ticket in tickets)
                Finish(ticket.SubAgent, SubAgentStatus.Cancelled, "Cancelled by user.", ticket.OwnerSubAgent);

            return VsToolResult.Cancelled(BuiltInToolEnum.DelegateTask);
        }
        finally
        {
            foreach (var ticket in tickets)
            {
                ticket.Handler.CancelPendingApprovals();
                ticket.SubAgent.ReleaseMemory(); // also disposes the branch CTS
            }
        }

        return ComposeResult(tickets);
    }

    /// <summary>
    /// Aggregates the finished batch: finalizes every branch state and composes the unified
    /// text for the model. A single task returns its answer verbatim; several tasks get a
    /// light per-task header so the model can map each answer to its task.
    /// </summary>
    private VsToolResult ComposeResult(SubAgentTicket[] tickets)
    {
        var sb = new System.Text.StringBuilder();
        var success = true;

        foreach (var ticket in tickets)
        {
            var status = ticket.Failed ? SubAgentStatus.Failed : SubAgentStatus.Completed;
            Finish(ticket.SubAgent, status, ticket.ErrorMessage, ticket.OwnerSubAgent);
            success &= !ticket.Failed;

            logger.LogInformation("Sub-agent branch {Index}/{Total} completed. Tokens: {Tokens}",
                ticket.Index + 1, tickets.Length, ticket.SubAgent.TotalTokens);

            if (tickets.Length == 1)
            {
                sb.Append(ticket.ResultText ?? string.Empty);
            }
            else
            {
                sb.AppendLine($"### Task {ticket.Index + 1}: {ticket.ShortTask}")
                  .AppendLine(ticket.ResultText ?? string.Empty)
                  .AppendLine();
            }
        }

        var resultText = sb.ToString();
        return new VsToolResult
        {
            Name = BuiltInToolEnum.DelegateTask,
            Success = success,
            Result = resultText.Length > 0 ? resultText : EmptyResponse,
            ErrorMessage = success ? string.Empty : "One or more delegated task(s) failed."
        };
    }

    private SubAgentTicket[] CreateTickets(
        List<SubAgentTaskDefinition> tasks, ToolCall toolCall, int depth, SubAgentMessage? ownerSubAgent)
    {
        var tickets = new SubAgentTicket[tasks.Count];

        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            logger.LogInformation("Sub-agent branch started (depth {Depth}, branch {Index}/{Total}). Task: {Task}",
                depth, i + 1, tasks.Count, task.Task);

            var handler = new ToolCallHandler(toolManager);
            var subAgent = new SubAgentMessage
            {
                Task = task.Task,
                SystemPrompt = task.SystemPrompt,
                AllowedTools = task.AllowedTools,
                Status = SubAgentStatus.Running,
                IsExpanded = true, // auto-expand while running so the user sees progress
                ToolCallHandler = handler,
            };

            toolCall.SubAgents ??= [];
            toolCall.SubAgents.Add(subAgent);

            tickets[i] = new SubAgentTicket(i, depth, task, subAgent, handler, ownerSubAgent);

            // Direct re-render request for the ToolCallBlock that OWNS this tool call. Parent
            // re-render cascades cannot be relied upon: the Blazor renderer skips unchanged-
            // parameter components, so an owner bump alone may never reach nested blocks.
            toolCall.NotifySubAgentsChanged();

            // For depth 0: AiChat re-renders, the top-level ToolCallBlock subscribes to
            // StateChanged and SubAgentView starts rendering the branches.
            // For nested batches: nobody listens to the orchestrator-level event, so bump the
            // OWNER sub-agent instead - its SubAgentView re-renders the branch history, which
            // (re)creates the nested ToolCallBlocks with live event subscriptions.
            NotifyStructuralChange(subAgent);
            ownerSubAgent?.NotifyStateChanged();
        }

        return tickets;
    }

    /// <summary>
    /// One branch run: a full tool-invocation loop through <see cref="AfAgentLoop"/> with an
    /// isolated handler/session. Deltas stream into the branch's <see cref="SubAgentMessage"/>
    /// (one VisualChatMessage per assistant turn, same semantics as ChatService.ProcessStreamAsync
    /// on the main loop). The resulting text lands on the ticket for <see cref="ComposeResult"/>.
    /// </summary>
    private async Task RunBranchAsync(SubAgentTicket ticket, CancellationToken cancellationToken)
    {
        var subAgent = ticket.SubAgent;
        var handler = ticket.Handler;
        handler.ApprovalRequired += id => OnApprovalRequired(subAgent, id);

        // Hard cap on total execution time per branch (legacy parity). Distinguished from
        // user cancellation: the root token fires only on real chat-level cancellation.
        // The RUN uses timeoutCts.Token: it fires both on the per-branch time limit and on
        // cancellation propagating down from the root.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeLimitSeconds = profileManager.ActiveProfile.MaxExecutionTimePerSubAgent;
        if (timeLimitSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeLimitSeconds));
        subAgent.SetCancellationTokenSource(timeoutCts);

        try
        {
            var session = new ConversationSession
            {
                Id = $"subagent_{DateTime.Now:s}_{Guid.NewGuid():N}",
                Mode = AppMode.Agent
            };
            subAgent.AttachSession(session); // message mutations propagate to the session
            AddUserMessage(subAgent, ticket.Task.Task);

            var systemPrompt = await systemPromptBuilder.PrepareSubAgentSystemPromptAsync(
                ticket.Task.SystemPrompt, timeoutCts.Token).ConfigureAwait(false);
            var tools = BuildSubAgentTools(ticket.Task.AllowedTools, ticket.Depth, subAgent).ToList();

            NotifyStructuralChange(subAgent);

            // Retry loop around the LLM call (AIChat.HandleErrorAsync parity): a transient
            // failure shows the countdown badges (IsRetrying/RetryAttempt/RetryCountdown) and
            // re-runs the call; OperationCanceledException is never retried (user cancellation
            // and the per-branch time limit are handled by the outer catches below).
            var maxRetries = commonSettingsProvider.Current.MaxRetries;
            for (var attempt = 1; ; attempt++)
            {
                var tokensToCompress = profileManager.ActiveProfile.TokensToCompress;
                if (tokensToCompress > 0 && session.TotalTokens > tokensToCompress)
                {
                    subAgent.IsCompressing = true;
                    subAgent.NotifyStateChanged();
                    try
                    {
                        await foreach (var _ in chatService.CompressSessionAsync(session, new CompletionsResult(), timeoutCts.Token).ConfigureAwait(false)) { }
                    }
                    finally
                    {
                        subAgent.IsCompressing = false;
                        subAgent.NotifyStateChanged();
                    }
                }

                var messages = session.GetFormattedMessages(systemPrompt, contentFilter);

                var capture = new CompletionsResult();
                var assistant = VisualChatMessage.CreateStreaming();
                subAgent.AddMessage(assistant);
                subAgent.NotifyStateChanged();

                var gateway = new AfAgentLoop(
                    () => profileManager.ActiveProfile,
                    httpClient,
                    handler)
                {
                    // Iteration cap continuation (legacy parity): 0 = unlimited.
                    MaximumIterations = profileManager.ActiveProfile.MaxIterationsPerSubAgent > 0
                        ? profileManager.ActiveProfile.MaxIterationsPerSubAgent
                        : null
                };

                // Mirror of AIChat.GetAiResponseInternalAsync: `current` is reassigned by
                // every onNewTurn so all delta callbacks always target the streaming message.
                var current = assistant;

                // Trailing throttle for the per-token UI event: every delta (content,
                // reasoning, state) raises NotifyStateChanged, and each raise re-renders
                // EVERY subscriber (SubAgentView + owning ToolCallBlock). At token rate
                // that floods the WASM sync queue and freezes clicks. Throttling the
                // SOURCE coalesces the storm into ~10 events/sec for all subscribers at
                // once; the trailing fire guarantees the final state always lands.
                // Structural changes (attach, finish, approval, retry) fire directly.
                using var uiThrottle = new EventThrottler(subAgent.NotifyStateChanged, SubAgentUiIntervalMs);

                void Touch(VisualChatMessage message)
                {
                    message.IsShouldRender = true;
                    subAgent.TotalTokens = session.TotalTokens;
                    uiThrottle.Notify();
                }

                try
                {
                    await chatService.ProcessStreamAsync(
                        assistant,
                        gateway.GetCompletionsAsync(messages, tools, session, capture, timeoutCts.Token),
                        onContentUpdate: _ => Touch(current),
                        onToolCallsUpdate: toolCalls =>
                        {
                            current.ToolCalls = toolCalls;
                            Touch(current);
                        },
                        onStateChange: () =>
                        {
                            current.Model ??= capture.Model;
                            subAgent.TotalTokens = session.TotalTokens;
                            // Reasoning-only deltas (и пустые маркеры) приходят только сюда:
                            // без Touch текущий Revision не растёт и SubAgentView.HasChanges()
                            // отсекает рендер по fast-path — блок размышлений не обновляется
                            // при стриминге. Touch идемпотентен для content-дельт
                            // (IsShouldRender + NotifyStateChanged уже вызываются там).
                            Touch(current);
                        },
                        cancellationToken: timeoutCts.Token,
                        onNewTurn: () =>
                        {
                            var next = VisualChatMessage.CreateStreaming();
                            subAgent.AddMessage(next);
                            subAgent.NotifyStateChanged();
                            current = next;
                            return next;
                        },
                        resultCapture: capture).ConfigureAwait(false);

                    break; // success
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt >= maxRetries)
                        throw; // retry budget exhausted: the outer catch records the branch failure

                    logger.LogWarning(ex, "Sub-agent branch {Index} attempt {Attempt}/{MaxRetries} failed, retrying.",
                        ticket.Index, attempt, maxRetries);

                    subAgent.IsRetrying = true;
                    subAgent.RetryAttempt = attempt;
                    subAgent.MaxRetryAttempts = maxRetries;
                    subAgent.NotifyStateChanged();

                    var delay = retryHandler.GetRetryDelay(attempt);
                    try
                    {
                        await retryHandler.WaitForRetryAsync(delay, i =>
                        {
                            subAgent.RetryCountdown = i;
                            subAgent.NotifyStateChanged();
                        }, timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // cancellation while waiting is NOT retried
                    }
                    finally
                    {
                        subAgent.IsRetrying = false;
                        subAgent.RetryCountdown = 0;
                        subAgent.NotifyStateChanged();
                    }

                    // Close the abandoned streaming assistant message before the next attempt.
                    assistant.IsStreaming = false;
                    assistant.IsShouldRender = true;
                }
            }

            ticket.ResultText = LastAssistantContent(subAgent);

            // Token budget (MaxTokensPerSubAgent): 0 = disabled. An over-budget branch still
            // reports its answer, with a warning appended for the parent agent.
            var budget = profileManager.ActiveProfile.MaxTokensPerSubAgent;
            if (budget > 0 && session.TotalTokens > budget)
                ticket.ResultText += $"(Token budget exceeded: {session.TotalTokens} / {budget} tokens.)";

            logger.LogInformation("Sub-agent branch {Index} finished. Tokens: {Tokens}",
                ticket.Index, session.TotalTokens);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Real chat-level/user-level cancellation: rethrow so the whole run unwinds.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Per-branch time limit fired mid-run: graceful completion with a report
            // for the parent agent (legacy parity). Same token-budget check as the success
            // path (session is out of scope here - the subAgent mirror is kept in sync).
            var budget = profileManager.ActiveProfile.MaxTokensPerSubAgent;
            var budgetNote = budget > 0 && subAgent.TotalTokens > budget
                ? $"(Token budget exceeded: {subAgent.TotalTokens} / {budget} tokens.)"
                : string.Empty;
            var message = $"Sub-agent exceeded execution time limit " +
                          $"({(DateTime.Now - subAgent.StartedAt).TotalMinutes:F1} / {timeLimitSeconds / 60.0:F0} minutes). " +
                          $"Last response: {LastAssistantContent(subAgent)}" + budgetNote;
            ticket.ResultText = message;
            logger.LogInformation("Sub-agent branch {Index} exceeded execution time limit mid-run.", ticket.Index);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sub-agent branch {Index} failed.", ticket.Index);
            ticket.ErrorMessage = ex.Message;
            ticket.ResultText = $"(Sub-agent branch failed: {ex.Message})";
        }
        finally
        {
            handler.CancelPendingApprovals();
        }
    }

    private void Finish(SubAgentMessage subAgent, SubAgentStatus status, string? errorMessage = null, SubAgentMessage? ownerSubAgent = null)
    {
        subAgent.Status = status;
        subAgent.CompletedAt = DateTime.Now;
        subAgent.ErrorMessage = errorMessage;
        if (status != SubAgentStatus.Failed)
            subAgent.IsExpanded = false;

        subAgent.NotifyStateChanged();
        // Nested batches: refresh the owner's SubAgentView (badges live inside its history).
        ownerSubAgent?.NotifyStateChanged();
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

    private static VisualChatMessage AddUserMessage(SubAgentMessage subAgent, string content)
    {
        var message = VisualChatMessage.CreateUser(content);
        subAgent.AddMessage(message);
        subAgent.NotifyStateChanged();
        return message;
    }

    private static string LastAssistantContent(SubAgentMessage subAgent)
        => subAgent.GetMessages()
            .LastOrDefault(m => m.Role == ChatMessageRole.Assistant)?.Content is { Length: > 0 } content
            ? content
            : EmptyResponse;

    /// <summary>
    /// Parses both argument shapes: the 'tasks' array (the current schema)
    /// and the legacy single-task shape (task / systemPrompt / allowedTools).
    /// </summary>
    private static List<SubAgentTaskDefinition> ParseTasks(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return [];

        try
        {
            var parsed = JsonSerializer.Deserialize<DelegateBatch>(argsJson, BatchJson) ?? new DelegateBatch();

            var tasks = parsed.Tasks is { Length: > 0 } ? parsed.Tasks
                : parsed.Task is not null ? [parsed.Task]
                : [];

            return tasks.Where(t => !string.IsNullOrWhiteSpace(t?.Task)).Select(t => t!).ToList();
        }
        catch (JsonException)
        {
            // Truncated arguments (stream cut mid-escape-sequence): repair what we can —
            // a partially recovered batch is better than failing the whole call.
            var repaired = JsonUtils.DeserializePartialAnonymousType(argsJson, new DelegateBatch());
            var tasks = repaired.Tasks is { Length: > 0 } ? repaired.Tasks
                : repaired.Task is not null ? [repaired.Task]
                : [];

            return tasks.Where(t => !string.IsNullOrWhiteSpace(t?.Task)).Select(t => t!).ToList();
        }
    }

    private static readonly JsonSerializerOptions BatchJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class DelegateBatch
    {
        public SubAgentTaskDefinition[]? Tasks { get; set; }

        // Legacy single-task shape
        public SubAgentTaskDefinition? Task { get; set; }
        public string? SystemPrompt { get; set; }
        public string[]? AllowedTools { get; set; }
    }

    /// <summary>
    /// Filters Agent-mode tools for a branch: category exclusions + optional whitelist.
    /// delegate_task INCLUDED while depth allows recursion (branch delegate tool runs this
    /// orchestrator again with depth + 1); ModeSwitch always excluded.
    /// </summary>
    private IEnumerable<Tool> BuildSubAgentTools(string[]? allowedTools, int depth, SubAgentMessage? ownerSubAgent)
    {
        var recursion = depth < profileManager.ActiveProfile.MaxSubAgentDepth;
        var whitelist = allowedTools is { Length: > 0 } ? allowedTools : null;

        foreach (var tool in toolManager.GetEnabledTools(AppMode.Agent))
        {
            if (tool.Category == ToolCategory.SubAgent || ExcludedCategories.Contains(tool.Category))
                continue;
            if (whitelist is not null && !whitelist.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            yield return tool;
        }

        if (recursion && (whitelist is null ||
                          whitelist.Contains(BuiltInToolEnum.DelegateTask, StringComparer.OrdinalIgnoreCase)))
        {
            yield return CreateBranchDelegateTool(depth, ownerSubAgent);
        }
    }

    /// <summary>
    /// A depth-aware delegate_task tool for a branch: executing it runs this orchestrator
    /// recursively with depth + 1, so delegated branches can spawn their own fan-outs.
    /// The branch's own SubAgentMessage is passed as the owner: nested batches bump it so
    /// the owner's SubAgentView re-renders and the nested sub-agent UI stays live.
    /// </summary>
    private Tool CreateBranchDelegateTool(int depth, SubAgentMessage? ownerSubAgent)
    {
        Tool self = new()
        {
            Name = BuiltInToolEnum.DelegateTask,
            DisplayName = SharedResource.ToolDelegateTask,
            NativeTool = BuiltInToolDefs.MapMethodToTool(nameof(BuiltInToolDefs.DelegateTask)),
            Category = ToolCategory.SubAgent,
            ExecuteAsync = (args, _) => Task.FromResult(new VsToolResult
            {
                Name = BuiltInToolEnum.DelegateTask,
                Success = false,
                ErrorMessage = SubAgentErrors.ToolCallContextRequired
            }),
            ExecuteWithContextAsync = (args, toolCall, ct) =>
            {
                toolCall.SubAgentDepth = depth + 1;
                return RunBatchAsync(args ?? "{}", toolCall, depth + 1, ownerSubAgent, ct);
            }
        };
        return self;
    }

    /// <summary>
    /// One delegated task bound to its UI message and tool handler.
    /// Branch result/flags are published here as <see cref="RunBranchAsync"/> runs.
    /// </summary>
    private sealed class SubAgentTicket(int index, int depth, SubAgentTaskDefinition task,
        SubAgentMessage subAgent, ToolCallHandler handler, SubAgentMessage? ownerSubAgent)
    {
        public int Index { get; } = index;

        /// <summary>Nesting depth of this branch (0 = delegated by the main agent).</summary>
        public int Depth { get; } = depth;

        public SubAgentTaskDefinition Task { get; } = task;

        public SubAgentMessage SubAgent { get; } = subAgent;

        public ToolCallHandler Handler { get; } = handler;

        /// <summary>
        /// SubAgentMessage of the branch that created this batch ('owner').
        /// Null for depth-0 batches (owned implicitly by the main chat). Bumping the owner
        /// re-renders its SubAgentView, which (re)subscribes the nested ToolCallBlocks.
        /// </summary>
        public SubAgentMessage? OwnerSubAgent { get; } = ownerSubAgent;

        /// <summary>Final branch answer text (timeout report / failure report included).</summary>
        public volatile string? ResultText;

        /// <summary>Failure message when the branch errored out.</summary>
        public volatile string? ErrorMessage;

        public bool Failed => ErrorMessage is not null;

        /// <summary>Task header for multi-task result blocks.</summary>
        public string ShortTask
        {
            get
            {
                var t = Task.Task?.Trim() ?? string.Empty;
                var nl = t.IndexOf('\n');
                var firstLine = nl > 0 ? t[..nl] : t;
                return firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;
            }
        }
    }
}
