namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Sub-agents run as parallel branches (Task.WhenAll fan-out).
/// One delegate_task call carries a list of tasks; each task becomes a branch that runs the
/// SAME turn loop as the main agent (one LLM call per turn through
/// <see cref="IChatService.GetTurnCompletionsAsync"/>, tools executed between the turns), all
/// branches run concurrently and the call returns only after every branch has finished. Nested delegation (recursion)
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
                if (!ticket.SubAgent.IsFinished)
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
            // The branch already finalized its own status in RunBranchAsync's finally
            // (immediate UI feedback for early finishers); only fill in the rest.
            if (!ticket.SubAgent.IsFinished)
            {
                var status = ticket.Failed ? SubAgentStatus.Failed : SubAgentStatus.Completed;
                Finish(ticket.SubAgent, status, ticket.ErrorMessage, ticket.OwnerSubAgent);
            }
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
                IsExpanded = false, // TODO хорошо бы добавить в настройки - открывать ли сабагентов при запуске
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
    /// Result content of a tool call that never produced one (batch interrupted by
    /// cancellation, an exception or an unresolved approval).
    /// </summary>
    private const string InterruptedToolResultContent =
        "The tool call was not executed (run interrupted before the result was produced).";

    /// <summary>
    /// One branch run: the SAME turn protocol as the main agent (<c>AiChat.RunTurnsAsync</c>)
    /// with an isolated handler/session.
    /// <para>
    /// Each iteration performs exactly ONE LLM call through
    /// <see cref="IChatService.GetTurnCompletionsAsync"/> (no hidden tool loop behind it) and
    /// executes the tool calls of that turn in between. The request history is rebuilt from the
    /// branch session at the start of EVERY turn, so compression, edits, deletions, interjects
    /// and mode-dependent tool sets applied between turns reach the branch model without any
    /// extra sync plumbing.
    /// </para>
    /// <para>
    /// Deltas stream into the branch's <see cref="SubAgentMessage"/> (one VisualChatMessage per
    /// assistant turn, created lazily on the first payload). The resulting text lands on the
    /// ticket for <see cref="ComposeResult"/>.
    /// </para>
    /// </summary>
    private async Task RunBranchAsync(SubAgentTicket ticket, CancellationToken cancellationToken)
    {
        var subAgent = ticket.SubAgent;
        var handler = ticket.Handler;
        handler.ApprovalRequired += id => OnApprovalRequired(subAgent, id);
        handler.ApprovalResolved += id => OnApprovalResolved(subAgent, id);

        // Hard cap on total execution time per branch (legacy parity). Distinguished from
        // user cancellation: the root token fires only on real chat-level cancellation.
        // The RUN uses timeoutCts.Token: it fires both on the per-branch time limit and on
        // cancellation propagating down from the root.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeLimitSeconds = profileManager.ActiveProfile.MaxExecutionTimePerSubAgent;
        if (timeLimitSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeLimitSeconds));
        subAgent.SetCancellationTokenSource(timeoutCts);

        var runToken = timeoutCts.Token;

        // ONE trailing throttle per RUN (not per turn): every delta of every turn raises
        // NotifyStateChanged, and each raise re-renders EVERY subscriber (SubAgentView + the
        // owning ToolCallBlock). At token rate that floods the WASM sync queue and freezes
        // clicks, so the SOURCE is coalesced into ~10 events/sec for all subscribers at once.
        // Structural changes (attach, finish, approval, retry) fire directly.
        using var uiThrottle = new Throttle(subAgent.NotifyStateChanged, SubAgentUiIntervalMs);

        try
        {
            var session = new ConversationSession
            {
                Id = $"subagent_{DateTime.Now:s}_{Guid.NewGuid():N}",
                Mode = AppMode.Agent
            };
            subAgent.AttachSession(session); // message mutations propagate to the session

            // Branch instructions. The per-turn seam builds the wire from the SESSION only, so
            // the sub-agent prompt (the LLM-provided custom prompt + rules/skills/solution
            // context) travels as the leading System message of the branch history; it is not
            // rendered by SubAgentView (only user interjects and assistant turns are).
            var systemPrompt = await systemPromptBuilder
                .PrepareSubAgentSystemPromptAsync(ticket.Task.SystemPrompt, runToken)
                .ConfigureAwait(false);
            subAgent.AddMessage(new VisualChatMessage
            {
                Role = ChatMessageRole.System,
                Content = systemPrompt,
                IsExpanded = false
            });

            AddUserMessage(subAgent, ticket.Task.Task);

            // The branch's OWN tool list, passed EXPLICITLY to the seam: null would make it
            // resolve the enabled tools of the current MAIN mode.
            var allowedTools = BuildSubAgentTools(ticket.Task.AllowedTools, ticket.Depth, subAgent).ToList();

            NotifyStructuralChange(subAgent);

            // Iteration cap: 0 = unlimited (the 40-turn default of the retired AfAgentLoop is
            // NOT inherited — MaxIterationsPerSubAgent is authoritative for branches).
            var maxIterations = profileManager.ActiveProfile.MaxIterationsPerSubAgent;
            var run = new BranchRun(
                ticket,
                session,
                allowedTools,
                uiThrottle,
                runToken,
                commonSettingsProvider.Current.MaxRetries,
                maxIterations > 0 ? maxIterations : int.MaxValue);

            for (var iteration = 0; ; iteration++)
            {
                // Pause/cancel gate #1: BEFORE the LLM call. A user-paused branch stops making
                // progress immediately; cancellation (user / time limit) unwinds here too.
                await subAgent.WaitIfPausedAsync(runToken).ConfigureAwait(false);

                // Compression gate: checked before EVERY turn, so a long tool-heavy branch
                // compacts its own session instead of growing until the provider rejects it.
                await CompressBranchIfNeededAsync(run).ConfigureAwait(false);

                // Interject baseline for THIS turn: message identity (Revision as a cheap
                // gate), never a numeric index - compression rewrites the history and shifts
                // every position, an index would re-read old turns or miss the new one.
                var revisionBeforeTurn = session.Revision;
                var userIdsBeforeTurn = UserMessageIds(session);

                // Per-turn state: a FRESH capture and FRESH callbacks per turn (a stale closure
                // would keep writing the previous turn's capture - message.Model got lost).
                var capture = new CompletionsResult();
                var turn = await RunBranchTurnAsync(run, capture).ConfigureAwait(false);

                subAgent.TotalTokens = session.TotalTokens;
                subAgent.NotifyStateChanged();

                var calls = capture.AccumulatedToolCalls;
                if (calls is not { Count: > 0 })
                {
                    // A plain text turn ends the run - unless the user wrote into the branch
                    // while it streamed: that message must be answered by another turn.
                    if (!HasNewUserMessage(session, revisionBeforeTurn, userIdsBeforeTurn))
                        break;
                    continue;
                }

                if (!await ExecuteBranchToolBatchAsync(run, turn, calls).ConfigureAwait(false))
                {
                    // No further LLM call either way. ROOT cancellation must unwind the whole
                    // batch, so the delegate_task result reports Cancelled (the branch finally
                    // would otherwise leave a "completed" ticket next to a cancelled status);
                    // only a per-branch TIME LIMIT finishes gracefully with its own report.
                    cancellationToken.ThrowIfCancellationRequested();
                    break;
                }

                // Iteration cap: the calls of the cap turn were just EXECUTED (their results
                // belong into the history), now the run stops with a note for the parent.
                // iteration is 0-based, so turn number N is iteration + 1: comparing the raw
                // counter would allow ONE turn more than the limit.
                if (iteration + 1 >= run.IterationLimit)
                {
                    run.AddNote($"(Iteration limit reached ({run.IterationLimit} turns).)");
                    logger.LogWarning("Sub-agent branch {Index} stopped at the iteration limit ({Limit} turns).",
                        ticket.Index, run.IterationLimit);
                    break;
                }
            }

            ticket.ResultText = LastAssistantContent(subAgent) + run.NoteText;

            // Token budget (MaxTokensPerSubAgent): 0 = disabled. The budget covers the WHOLE
            // run (session.TotalTokens accumulates across all turns and tool round-trips).
            // An over-budget branch still reports its answer, with a warning for the parent.
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

            // A turn may be abandoned mid-stream (cancellation, exhausted retries): close it,
            // otherwise it stays a frozen bubble in SubAgentView forever.
            foreach (var message in subAgent.GetMessages())
            {
                if (!message.IsStreaming)
                    continue;
                message.IsStreaming = false;
                message.IsShouldRender = true;
            }

            // Per-branch finalize: a branch that finishes early must switch to its terminal
            // status immediately, not stay "Running" until the whole batch completes.
            // ComposeResult only fills in what is still missing (e.g. a branch that never
            // entered this method). Root cancellation is a CANCELLED branch, not a completed
            // one - the branch's own finally runs before RunBatchAsync can classify it.
            if (!subAgent.IsFinished)
            {
                var status = cancellationToken.IsCancellationRequested
                    ? SubAgentStatus.Cancelled
                    : ticket.Failed ? SubAgentStatus.Failed : SubAgentStatus.Completed;
                Finish(subAgent, status, ticket.ErrorMessage, ticket.OwnerSubAgent);
            }
        }
    }

    /// <summary>
    /// Streams exactly ONE branch turn into its own message and returns that message.
    /// <para>
    /// The turn message is created LAZILY: the object exists from the start (it is the stream
    /// target and the message excluded from its own request by reference), but it enters the
    /// branch history on the FIRST payload. A turn that ends without payload therefore leaves
    /// nothing behind - no empty bubble and no empty assistant turn in the next request.
    /// </para>
    /// <para>
    /// Retries happen HERE, around one turn, and share the RUN-level counter: a branch may
    /// burn its whole retry budget across turns, but never N retries per turn. Before a retry
    /// the failed turn is dropped from the history BY ID, otherwise the partially streamed
    /// answer would ride into the next request. A turn that arrives EMPTY (no text, no
    /// reasoning, no tool calls) is a failed turn for the same purpose: it spends the same
    /// budget, because a silent model is not the answer the parent delegated for.
    /// </para>
    /// </summary>
    private async Task<VisualChatMessage> RunBranchTurnAsync(BranchRun run, CompletionsResult capture)
    {
        while (true)
        {
            var turn = VisualChatMessage.CreateStreaming();
            var materialized = false;

            // The ONLY place the turn message enters the branch history.
            void Materialize()
            {
                if (materialized || !HasPayload(turn))
                    return;
                materialized = true;
                run.SubAgent.AddMessage(turn);
                run.SubAgent.NotifyStateChanged();
            }

            try
            {
                await chatService.ProcessStreamAsync(
                    turn,
                    chatService.GetTurnCompletionsAsync(run.Session, turn, run.Tools, capture, run.Token),
                    onContentUpdate: _ =>
                    {
                        run.Touch(turn);
                        Materialize();
                    },
                    onToolCallsUpdate: toolCalls =>
                    {
                        // Same objects: the results render live on this turn message.
                        turn.ToolCalls = toolCalls;
                        run.Touch(turn);
                        Materialize();
                    },
                    onStateChange: () =>
                    {
                        turn.Model ??= capture.Model;
                        // Reasoning-only deltas arrive ONLY here: without Touch the branch
                        // Revision does not grow and SubAgentView skips the render, leaving the
                        // thinking block frozen while it streams.
                        run.Touch(turn);
                        Materialize();
                    },
                    capture,
                    run.Token).ConfigureAwait(false);

                // ProcessStreamAsync closes the message and stamps the per-turn timings.
                turn.Model ??= capture.Model;

                if (!HasPayload(turn))
                {
                    // Silent model: an ATTEMPT, not an answer. The turn never materialized, so the
                    // history holds no trace of it - only budget is spent.
                    if (await TrySpendRetryAttemptAsync(run, failure: null).ConfigureAwait(false))
                        continue;

                    // The empty marker alone cannot say whether the model was silent once or ten
                    // times, so the attempt count goes to the parent as a note.
                    run.AddNote($"(No output after {run.RetriesUsed} attempt(s).)");
                    logger.LogWarning(
                        "Sub-agent branch {Index} returned an empty response and the retry budget is spent.",
                        run.Index);
                    return turn;
                }

                Materialize();
                turn.IsShouldRender = true;
                run.SubAgent.NotifyStateChanged();
                return turn;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The failure must be visible: materialize the message that carries it.
                if (!materialized)
                {
                    materialized = true;
                    run.SubAgent.AddMessage(turn);
                }

                // Only a failure that can succeed on a repeat may spend the budget: a wrong API
                // key or a rejected request answers identically ten times, and a non-LlmApiException
                // is our own bug. Failing at once keeps the branch's tokens and time for the
                // parent agent's fallback instead of burning them on a certain failure.
                var retryable = ex is LlmApiException { IsRetryable: true };

                if (!retryable || !await TrySpendRetryAttemptAsync(run, ex).ConfigureAwait(false))
                {
                    // Retry budget of the WHOLE run exhausted (or spent needlessly): the outer
                    // catch records the branch failure. Tool calls of the abandoned turn still
                    // need an answer, otherwise the wire history has assistant(tool_calls)
                    // without results.
                    EnsureToolResults(turn.ToolCalls);
                    throw;
                }

                // Drop the failed turn: the session BY ID first (the mirror removal below then
                // becomes a no-op for the session), then the UI copy. Reusing the object would
                // duplicate its content; keeping it would poison the next request.
                run.Session.RemoveMessage(turn.Id);
                run.SubAgent.RemoveMessage(turn);
            }
        }
    }

    /// <summary>
    /// Spends ONE attempt of the RUN-level budget and waits out the backoff with the badge armed.
    /// <para>
    /// A thrown failure and an empty answer draw from the SAME counter on purpose: the budget belongs
    /// to the run, so a branch can never get N retries per turn.
    /// </para>
    /// </summary>
    /// <param name="failure">The exception that ended the attempt, or null for an empty answer.</param>
    /// <returns>True to retry; false when the budget is gone (the caller decides how the branch ends).</returns>
    private async Task<bool> TrySpendRetryAttemptAsync(BranchRun run, Exception? failure)
    {
        var attempt = ++run.RetriesUsed;

        // The last allowed attempt reports the outcome; retrying past it would exceed the budget by
        // one call.
        if (attempt >= run.MaxRetries)
            return false;

        if (failure is null)
            logger.LogWarning(
                "Sub-agent branch {Index} attempt {Attempt}/{MaxAttempts} produced no output, retrying.",
                run.Index, attempt, run.MaxRetries);
        else
            logger.LogWarning(failure,
                "Sub-agent branch {Index} attempt {Attempt}/{MaxAttempts} failed, retrying.",
                run.Index, attempt, run.MaxRetries);

        run.SubAgent.IsRetrying = true;
        run.SubAgent.RetryAttempt = attempt;
        run.SubAgent.MaxRetryAttempts = run.MaxRetries;
        run.SubAgent.NotifyStateChanged();

        try
        {
            var delay = retryHandler.GetRetryDelay(attempt);
            await retryHandler.WaitForRetryAsync(delay, i =>
            {
                run.SubAgent.RetryCountdown = i;
                run.SubAgent.NotifyStateChanged();
            }, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation while waiting is NOT retried
        }
        finally
        {
            run.SubAgent.IsRetrying = false;
            run.SubAgent.RetryCountdown = 0;
            run.SubAgent.NotifyStateChanged();
        }

        return true;
    }

    /// <summary>
    /// Compresses the branch session when it crossed <c>TokensToCompress</c>. The summary stream
    /// is swallowed here (only the badge is visible); <see cref="SubAgentMessage.IsCompressing"/>
    /// is raised around the call because <see cref="SubAgentMessage.TryAddInterject"/> must
    /// reject interjects while the history is being rewritten from a snapshot.
    /// </summary>
    private async Task CompressBranchIfNeededAsync(BranchRun run)
    {
        var tokensToCompress = profileManager.ActiveProfile.TokensToCompress;
        if (tokensToCompress <= 0 || run.Session.TotalTokens <= tokensToCompress)
            return;

        var subAgent = run.SubAgent;
        subAgent.IsCompressing = true;
        subAgent.NotifyStateChanged();
        try
        {
            await foreach (var _ in chatService
                .CompressSessionAsync(run.Session, new CompletionsResult(), run.Token)
                .ConfigureAwait(false))
            {
            }
        }
        finally
        {
            subAgent.IsCompressing = false;
            subAgent.NotifyStateChanged();
        }
    }

    /// <summary>
    /// Executes the tool calls of ONE branch turn through the existing approval/execution path.
    /// <para>
    /// Approvals are prepared EXACTLY ONCE for the whole batch: the method cancels every pending
    /// gate and re-registers the ask_user waiters first, so a second call would drop decisions
    /// the user already made.
    /// </para>
    /// <para>
    /// A <c>switch_mode</c> call inside the batch changes the mode for the REST of the batch
    /// (the handler receives ONE mode per call), so the batch is split at the switch and the
    /// remainder runs under the NEW mode.
    /// </para>
    /// </summary>
    /// <returns>
    /// True when the batch completed; false when it was cancelled or interrupted (the caller
    /// must not start another LLM turn then).
    /// </returns>
    private async Task<bool> ExecuteBranchToolBatchAsync(BranchRun run, VisualChatMessage turn, List<ToolCall> calls)
    {
        var subAgent = run.SubAgent;

        // Same objects: the results render live on this turn message.
        turn.ToolCalls = calls;
        turn.IsShouldRender = true;

        run.Handler.PrepareToolsForApprovals(calls);
        subAgent.NotifyStateChanged();

        var completed = true;
        try
        {
            var start = 0;
            while (start < calls.Count)
            {
                run.Token.ThrowIfCancellationRequested();

                // Up to and INCLUDING the next switch_mode call (or the whole remainder).
                var switchIndex = calls.FindIndex(start, call => call.Function.Name == BasicEnum.SwitchMode);
                var end = switchIndex < 0 ? calls.Count : switchIndex + 1;

                // Pause/cancel gate #2: BEFORE the tool execution. A paused branch does not run
                // the tool even if its approval arrived while paused.
                for (var i = start; i < end; i++)
                    await subAgent.WaitIfPausedAsync(run.Token).ConfigureAwait(false);

                await run.Handler
                    .ProcessToolCallsAsync(calls.GetRange(start, end - start), run.Session.Mode, run.Token)
                    .ConfigureAwait(false);

                start = end;
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            completed = false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sub-agent branch {Index} tool batch failed.", run.Index);
            run.AddNote($"(Tool batch failed: {ex.Message})");
            completed = false;
        }
        finally
        {
            // Whatever interrupted the batch, EVERY call needs a Result: an
            // assistant(tool_calls) turn without matching tool messages is a hard API 400 on
            // the next turn (and the branch continues with tool results in its history).
            EnsureToolResults(calls);
            turn.IsShouldRender = true;
            subAgent.TotalTokens = run.Session.TotalTokens;
            subAgent.NotifyStateChanged();
        }

        return completed && !run.Token.IsCancellationRequested;
    }

    /// <summary>
    /// True when the turn carries payload (text, reasoning or tool calls) - i.e. it is an ANSWER.
    /// Decides both whether the turn enters the branch history and whether it is retried.
    /// <para>
    /// Whitespace-only is nothing: `ConversationSession.IsEmptyAssistant` uses the same rule when it
    /// filters the wire, so accepting such a turn would only mean dropping it from the next request.
    /// </para>
    /// </summary>
    private static bool HasPayload(VisualChatMessage message)
        => !string.IsNullOrWhiteSpace(message.Content)
           || !string.IsNullOrWhiteSpace(message.ReasoningContent)
           || message.ToolCalls is { Count: > 0 };

    /// <summary>Ids of the user messages of the branch session (interject baseline).</summary>
    private static HashSet<string> UserMessageIds(ConversationSession session)
        => session.GetMessagesSnapshot()
            .Where(m => m.Role == ChatMessageRole.User)
            .Select(m => m.Id)
            .ToHashSet();

    /// <summary>
    /// True when the user wrote into the branch while the turn streamed. Detected by message
    /// identity (<see cref="ConversationSession.Revision"/> only as a cheap "something changed"
    /// gate), never by a numeric index: compression rewrites the history and shifts every
    /// position.
    /// </summary>
    private static bool HasNewUserMessage(ConversationSession session, long revisionBeforeTurn, HashSet<string> userIdsBeforeTurn)
    {
        if (session.Revision == revisionBeforeTurn)
            return false;

        return session.GetMessagesSnapshot()
            .Any(m => m.Role == ChatMessageRole.User && !userIdsBeforeTurn.Contains(m.Id));
    }

    /// <summary>
    /// Guarantees a Result for EVERY call of a batch: a batch interrupted by cancellation, an
    /// exception or an unresolved approval must not leave a tool call without an answer.
    /// </summary>
    private void EnsureToolResults(IEnumerable<ToolCall>? calls)
    {
        if (calls is null)
            return;

        foreach (var toolCall in calls)
        {
            if (toolCall.Result is not null)
                continue;

            toolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
            toolCall.Result = new ToolResult
            {
                Success = false,
                Content = InterruptedToolResultContent,
                Name = toolCall.Function.Name,
                DisplayName = ToolResult.GetDisplayName(false,
                    toolManager.GetTool(toolCall.Function.Name)?.DisplayName ?? toolCall.Function.Name)
            };
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
        // Blocked on user interaction (approval or ask_user): show the NeedAction badge
        // instead of Running while the branch waits.
        subAgent.Status = SubAgentStatus.NeedAction;
        subAgent.NotifyStateChanged();
        NotifyStructuralChange(subAgent);
    }

    private void OnApprovalResolved(SubAgentMessage subAgent, string toolCallId)
    {
        subAgent.PendingToolCallId = null;
        // Back to Running only when this branch has no more pending interactions;
        // another tool call may have raised NeedAction in the meantime.
        if (subAgent.Status == SubAgentStatus.NeedAction)
        {
            subAgent.Status = SubAgentStatus.Running;
            subAgent.NotifyStateChanged();
            NotifyStructuralChange(subAgent);
        }
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
    /// Run-scoped state of ONE branch run: the pinned session, the branch's own tool list, its
    /// tool handler and the RUN-level counters.
    /// <para>
    /// One instance is created per <see cref="RunBranchAsync"/> and shared by every turn. That
    /// is the point: the retry counter belongs to the RUN, so a branch may burn its whole retry
    /// budget across turns - it never gets N retries per turn (which would multiply the LLM
    /// calls and the tokens counted against <c>MaxTokensPerSubAgent</c>). The compression notes
    /// accumulated between the turns are reported to the parent agent once, with the answer.
    /// </para>
    /// </summary>
    private sealed class BranchRun(
        SubAgentTicket ticket,
        ConversationSession session,
        IReadOnlyList<Tool> tools,
        Throttle uiThrottle,
        CancellationToken token,
        int maxRetries,
        int iterationLimit)
    {
        private readonly List<string> _notes = [];

        public SubAgentMessage SubAgent { get; } = ticket.SubAgent;

        public ToolCallHandler Handler { get; } = ticket.Handler;

        public ConversationSession Session { get; } = session;

        /// <summary>Explicit tool list of this branch (never null - see RunBranchAsync).</summary>
        public IReadOnlyList<Tool> Tools { get; } = tools;

        /// <summary>Run token: fires on the branch time limit AND on root cancellation.</summary>
        public CancellationToken Token { get; } = token;

        /// <summary>Total attempts allowed for the WHOLE run (not per turn).</summary>
        public int MaxRetries { get; } = maxRetries;

        /// <summary>Turn cap of the run (<see cref="int.MaxValue"/> when unlimited).</summary>
        public int IterationLimit { get; } = iterationLimit;

        public int Index { get; } = ticket.Index;

        /// <summary>Retry attempts consumed by the WHOLE run.</summary>
        public int RetriesUsed;

        /// <summary>Appends a note reported to the parent agent together with the answer.</summary>
        public void AddNote(string note) => _notes.Add(note);

        /// <summary>All notes as one text (empty when nothing was recorded).</summary>
        public string NoteText => _notes.Count == 0 ? string.Empty : string.Join(" ", _notes);

        /// <summary>
        /// Per-delta UI touch: marks the streaming message for render and reports the change
        /// through the RUN-wide throttle (one subscriber notification per window for all
        /// messages of all turns).
        /// </summary>
        public void Touch(VisualChatMessage message)
        {
            message.IsShouldRender = true;
            uiThrottle.Notify();
        }
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
