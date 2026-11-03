namespace UIBlazor.Models;

/// <summary>
/// Represents a sub-agent execution context and its conversation.
/// Attached to the parent assistant message that triggered the delegate_task tool call.
/// </summary>
public class SubAgentMessage
{
    /// <summary>
    /// Unique identifier for this sub-agent execution.
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The task description given by the main agent to the sub-agent.
    /// </summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>
    /// The system prompt defining the sub-agent's role and expertise.
    /// </summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// List of tool names the sub-agent is allowed to use.
    /// If null or empty, all tools are available (except delegate_task which is always blocked).
    /// </summary>
    public string[]? AllowedTools { get; set; }

    /// <summary>
    /// Internal storage for sub-agent conversation messages.
    /// Access is guarded by <see cref="_messagesLock"/> to prevent data races
    /// between streaming threads (writers) and Blazor render thread (reader).
    /// </summary>
    [JsonIgnore]
    private readonly List<VisualChatMessage> _messages = [];

    /// <summary>
    /// Lock object protecting <see cref="_messages"/> from concurrent access.
    /// </summary>
    [JsonIgnore]
    private readonly object _messagesLock = new();

    /// <summary>
    /// The <see cref="ConversationSession"/> that mirrors this sub-agent's messages
    /// for LLM API calls (via <see cref="ConversationSession.GetFormattedMessages"/>).
    /// When attached, all message mutations (Add/Remove/Set) automatically propagate
    /// to the session, eliminating the need for callers to manually synchronize both lists.
    /// </summary>
    [JsonIgnore]
    private ConversationSession? _session;

    /// <summary>
    /// Attaches a <see cref="ConversationSession"/> so that message mutations on this
    /// <see cref="SubAgentMessage"/> automatically propagate to the session.
    /// This establishes <see cref="SubAgentMessage"/> as the single source of truth;
    /// the session becomes an automatically-synced mirror used only for LLM API calls.
    /// </summary>
    /// <param name="session">The session to attach. Must not be null.</param>
    public void AttachSession(ConversationSession session)
    {
        _session = session;

        // Reverse sync: context compression rewrites the session history directly via
        // SetMessages (bypassing the Add/Remove/Set wrapper methods), so the UI-side
        // _messages copy would go stale — the user would see the uncompressed history
        // while the model receives the compressed one. Listen to the session event and
        // update the local copy WITHOUT propagating back into the session (no recursion).
        session.MessagesReplaced += OnSessionMessagesReplaced;
    }

    /// <summary>
    /// Handler for <see cref="ConversationSession.MessagesReplaced"/>: replaces the local
    /// _messages copy with the compressed history. Writes only to the local list — the
    /// session is already updated (this is the event source).
    /// </summary>
    private void OnSessionMessagesReplaced(List<VisualChatMessage> messages)
    {
        lock (_messagesLock)
        {
            _messages.Clear();
            _messages.AddRange(messages);
        }
    }

    /// <summary>
    /// The conversation messages of the sub-agent (for UI display).
    /// Thread-safe: all operations (Add, Remove, Count, iteration) are guarded by a lock.
    /// Use the instance methods (AddMessage, RemoveMessage, GetMessageCount, GetMessages)
    /// instead of accessing the property directly for mutations and reads.
    /// The property getter returns a snapshot copy for safe iteration.
    /// </summary>
    [JsonIgnore]
    public List<VisualChatMessage> Messages
    {
        get
        {
            lock (_messagesLock)
                return [.. _messages];
        }
        set
        {
            lock (_messagesLock)
            {
                _messages.Clear();
                _messages.AddRange(value);
            }
        }
    }

    /// <summary>
    /// Adds a user interject message to the RUNNING sub-agent conversation ("write to the
    /// branch while it works"). The message lands in the UI history and the attached session
    /// immediately; the branch's <c>AfAgentLoop</c> picks it up at the next turn boundary
    /// (after the current tool turn completes, before the next LLM call).
    /// Rejected when the branch is not running or is compressing (compression rewrites the
    /// session history from a snapshot and would silently drop the interject).
    /// </summary>
    /// <returns>True when the interject was accepted.</returns>
    public bool TryAddInterject(string text)
    {
        if (Status is not (SubAgentStatus.Running or SubAgentStatus.NeedAction or SubAgentStatus.Paused) || IsCompressing)
            return false;

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        var message = VisualChatMessage.CreateUser(trimmed);
        message.IsInterject = true;
        AddMessage(message); // writes _messages + the attached session
        NotifyStateChanged();
        return true;
    }

    /// <summary>
    /// Thread-safe: adds a message to the sub-agent conversation.
    /// Automatically propagates to the attached <see cref="ConversationSession"/> (if any).
    /// </summary>
    public void AddMessage(VisualChatMessage message)
    {
        lock (_messagesLock)
        {
            _messages.Add(message);
            _session?.AddMessage(message);
        }
    }

    /// <summary>
    /// Thread-safe: removes a message from the sub-agent conversation.
    /// Automatically propagates to the attached <see cref="ConversationSession"/> (if any).
    /// </summary>
    public void RemoveMessage(VisualChatMessage message)
    {
        lock (_messagesLock)
        {
            _messages.Remove(message);
            _session?.RemoveMessage(message);
        }
    }

    /// <summary>
    /// Thread-safe: replaces all messages with the provided list.
    /// Automatically propagates to the attached <see cref="ConversationSession"/> (if any).
    /// </summary>
    public void SetMessages(List<VisualChatMessage> messages)
    {
        lock (_messagesLock)
        {
            _messages.Clear();
            _messages.AddRange(messages);
            _session?.SetMessages(messages);
        }
    }

    /// <summary>
    /// Thread-safe: returns the number of messages in the sub-agent conversation.
    /// </summary>
    public int GetMessageCount()
    {
        lock (_messagesLock)
            return _messages.Count;
    }

    /// <summary>
    /// Thread-safe: returns a snapshot copy of the messages for safe iteration.
    /// </summary>
    public List<VisualChatMessage> GetMessages()
    {
        lock (_messagesLock)
            return [.. _messages];
    }

    /// <summary>
    /// Thread-safe: returns true if any message matches the predicate.
    /// </summary>
    public bool AnyMessage(Func<VisualChatMessage, bool> predicate)
    {
        lock (_messagesLock)
            return _messages.Any(predicate);
    }

    /// <summary>
    /// Internal use only, action must not re-enter the lock.
    /// Thread-safe: performs an action on each message.
    /// </summary>
    public void ForEachMessage(Action<VisualChatMessage> action)
    {
        lock (_messagesLock)
        {
            foreach (var msg in _messages)
                action(msg);
        }
    }

    /// <summary>
    /// Current status of the sub-agent execution.
    /// </summary>
    public SubAgentStatus Status { get; set; } = SubAgentStatus.Pending;

    /// <summary>
    /// When the sub-agent started execution.
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// When the sub-agent completed (success, failure, or cancellation).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Total tokens used by the sub-agent.
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// Error message if the sub-agent failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether the sub-agent conversation is expanded in the UI.
    /// </summary>
    [JsonIgnore]
    public bool IsExpanded { get; set; }

    /// <summary>
    /// Whether the sub-agent context is currently being compressed.
    /// Used by UI to show a compression indicator badge.
    /// </summary>
    [JsonIgnore]
    public bool IsCompressing { get; set; }

    /// <summary>
    /// Whether the sub-agent is currently retrying an LLM call after a transient error.
    /// Used by UI to show a retry indicator badge.
    /// </summary>
    [JsonIgnore]
    public bool IsRetrying { get; set; }

    /// <summary>
    /// The current retry attempt number (1-based) when <see cref="IsRetrying"/> is true.
    /// </summary>
    [JsonIgnore]
    public int RetryAttempt { get; set; }

    /// <summary>
    /// The delay in seconds before the next retry attempt.
    /// Used by UI to show a countdown.
    /// </summary>
    [JsonIgnore]
    public int RetryDelaySeconds { get; set; }

    /// <summary>
    /// Maximum number of retry attempts for LLM calls.
    /// Used by UI to show "attempt / max" in the retry indicator.
    /// </summary>
    [JsonIgnore]
    public int MaxRetryAttempts { get; set; }

    /// <summary>
    /// Countdown value (in seconds) for the current retry delay.
    /// Updated every second by the retry countdown loop.
    /// When 0, no countdown is active.
    /// </summary>
    [JsonIgnore]
    public int RetryCountdown { get; set; }

    /// <summary>
    /// CancellationTokenSource linked to the parent token, allowing independent
    /// cancellation of this sub-agent without cancelling the entire chat.
    /// Set by <c>AfSubAgentOrchestrator</c> via <see cref="SetCancellationTokenSource"/>.
    /// </summary>
    [JsonIgnore]
    private CancellationTokenSource? _cancelSource;

    /// <summary>
    /// Sets the linked CancellationTokenSource so the sub-agent can be cancelled independently.
    /// Called by <c>AfSubAgentOrchestrator</c> after creating the linked token.
    /// Uses <see cref="Volatile.Write"/> to ensure the assignment is immediately visible
    /// to other threads (UI thread calling <see cref="Cancel"/>).
    /// </summary>
    public void SetCancellationTokenSource(CancellationTokenSource cts)
        => Volatile.Write(ref _cancelSource, cts);

    /// <summary>
    /// Cancels the sub-agent independently (without cancelling the parent chat).
    /// Safe to call multiple times and from any thread (typically the UI thread).
    ///
    /// Uses <see cref="Interlocked.Exchange"/> to atomically take ownership of the
    /// CancellationTokenSource. This prevents a race with <see cref="ReleaseMemory"/>
    /// (called from a background thread) where Cancel() could call .Cancel() on a
    /// CTS that ReleaseMemory() has already disposed, causing ObjectDisposedException.
    /// The thread that takes the CTS via Exchange owns it exclusively and is
    /// responsible for both cancelling and disposing it.
    /// </summary>
    public void Cancel()
    {
        var cts = Interlocked.Exchange(ref _cancelSource, null);
        if (cts is null)
            return;

        // The branch's `using var timeoutCts` may dispose this CTS concurrently on the
        // branch thread (race window between Exchange and using-dispose).
        // A paused loop waits on the pause gate with the linked token; releasing it
        // lets the cancelled WaitAsync throw immediately instead of waiting for the
        // token-only path.
        if (IsPaused)
        {
            IsPaused = false;
            try { _pauseGate.Release(); } catch (SemaphoreFullException) { }
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by the branch ending - nothing to signal.
        }
        finally
        {
            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Whether the sub-agent can currently be cancelled (i.e. it is still running
    /// or waiting for user action).
    /// </summary>
    [JsonIgnore]
    public bool CanCancel => Status is SubAgentStatus.Running or SubAgentStatus.NeedAction or SubAgentStatus.Paused;

    /// <summary>
    /// Whether the sub-agent is currently paused by the user (the agent loop waits
    /// at the next turn boundary until resumed). UI shows a pause/play toggle.
    /// </summary>
    [JsonIgnore]
    public bool IsPaused { get; private set; }

    [JsonIgnore]
    private readonly SemaphoreSlim _pauseGate = new(1, 1);

    /// <summary>
    /// The status the sub-agent had before pausing (Running or NeedAction),
    /// restored by <see cref="Resume"/>.
    /// </summary>
    [JsonIgnore]
    private SubAgentStatus _statusBeforePause;

    /// <summary>
    /// Pauses the sub-agent: the branch loop awaits <see cref="WaitIfPausedAsync"/>
    /// at the next turn boundary and stops making LLM calls until resumed.
    /// Sets the status to <see cref="SubAgentStatus.Paused"/> (the previous status is
    /// remembered and restored on resume). No-op when the sub-agent is not active
    /// or is already paused.
    /// </summary>
    public void Pause()
    {
        if (!CanCancel || IsPaused)
            return;

        // Acquire synchronously: the gate is free unless a resume raced in (then
        // Pause is a no-op anyway — the loop will not block).
        _pauseGate.Wait();
        IsPaused = true;
        _statusBeforePause = Status;
        Status = SubAgentStatus.Paused;
        NotifyStateChanged();
    }

    /// <summary>
    /// Resumes a paused sub-agent: releases the gate the loop is waiting on and
    /// restores the pre-pause status (Running or NeedAction). No-op when not paused.
    /// </summary>
    public void Resume()
    {
        if (!IsPaused)
            return;

        IsPaused = false;
        Status = _statusBeforePause is SubAgentStatus.NeedAction
            ? SubAgentStatus.NeedAction
            : SubAgentStatus.Running;
        _pauseGate.Release();
        NotifyStateChanged();
    }

    /// <summary>
    /// Called by the branch loop at turn boundaries: blocks (asynchronously) while
    /// the sub-agent is paused. Cancellation (user cancel / time limit) propagates
    /// as <see cref="OperationCanceledException"/> so the loop unwinds normally.
    /// </summary>
    public async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        if (!IsPaused)
            return;

        await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _pauseGate.Release();
    }

    /// <summary>
    /// True when the sub-agent has reached a terminal state and will not run anymore.
    /// </summary>
    [JsonIgnore]
    public bool IsFinished => Status is SubAgentStatus.Completed or SubAgentStatus.Cancelled or SubAgentStatus.Failed;

    /// <summary>
    /// The sub-agent's own ToolCallHandler for processing tool call approvals.
    /// Isolated from the main agent's ToolCallHandler to prevent approval waiter conflicts.
    /// UI components use this to route approval responses for sub-agent tool calls.
    /// <see cref="ReleaseMemory"/> cancels all pending approvals on it but keeps the reference
    /// to prevent late-arriving approvals from routing to the wrong (parent) handler.
    /// </summary>
    [JsonIgnore]
    public IToolCallHandler? ToolCallHandler { get; set; }

    /// <summary>
    /// ID of the tool call currently requiring user approval or ask_user interaction.
    /// Set when the sub-agent's ToolCallHandler fires ApprovalRequired.
    /// Cleared when AiChat processes the notification.
    /// Used by AiChat to scroll to the tool and show a notification.
    /// </summary>
    [JsonIgnore]
    public string? PendingToolCallId { get; set; }

    /// <summary>
    /// Event raised when the sub-agent state changes (status, messages, etc.)
    /// UI components subscribe to this to trigger re-rendering.
    /// Thread-safe: uses Volatile.Read to prevent NullReferenceException
    /// when invoked from parallel streaming threads.
    /// </summary>
    public event Action? StateChanged;

    [JsonIgnore]
    private long _revision;

    /// <summary>
    /// Monotonically increasing revision number, incremented by every
    /// <see cref="NotifyStateChanged"/> call. UI components compare it against
    /// their last-seen value for an O(1) change check instead of scanning all
    /// messages on every <c>ShouldRender</c> (which runs on every parent render,
    /// including each token of the main agent stream).
    /// </summary>
    [JsonIgnore]
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>
    /// Raises the StateChanged event to notify UI subscribers.
    /// Thread-safe invocation via Volatile.Read pattern.
    /// </summary>
    public void NotifyStateChanged()
    {
        Interlocked.Increment(ref _revision);
        Volatile.Read(ref StateChanged)?.Invoke();
    }

    /// <summary>
    /// Releases heavy runtime resources that are no longer needed after the sub-agent
    /// has finished (Completed / Cancelled / Failed).
    ///
    /// This method is called by <c>AfSubAgentOrchestrator</c> in its <c>finally</c> block,
    /// AFTER <c>CancelPendingApprovals</c> has already cancelled all waiters.
    ///
    /// What is cleaned up:
    /// <list type="bullet">
    /// <item><b>ToolCallHandler</b> — all approval waiters are already cancelled;
    ///   no new approvals can arrive after the sub-agent loop exits.
    ///   The handler also holds a reference to <c>IToolManager</c> which we can release.</item>
    /// <item><b>PendingToolCallId</b> — only relevant while the sub-agent is running.</item>
    /// <item><b>Segments</b> in each message — <c>ContentSegment</c> objects contain
    ///   <c>StringBuilder</c>, <c>Dictionary</c>, and <c>List&lt;string&gt;</c> used only
    ///   during streaming. <c>SubAgentView</c> renders via <c>msg.Content</c> directly,
    ///   not via <c>Segments</c>.</item>
    /// <item><b>Transient flags</b> (<c>IsShouldRender</c>, <c>IsStreaming</c>,
    ///   <c>TempContent</c>, <c>DisplayContent</c>) — no longer needed after completion.</item>
    /// </list>
    ///
    /// What is PRESERVED (needed for UI display when user expands the sub-agent):
    /// <list type="bullet">
    /// <item><b>Messages list</b> — the full conversation is kept so the user can
    ///   expand and review the sub-agent's reasoning chain.</item>
    /// <item><b>Content</b> and <b>ReasoningContent</b> in each message —
    ///   displayed by <c>SubAgentView</c> via <c>MarkdownBlock</c>.</item>
    /// <item><b>ToolCalls</b> and their <b>Result</b> — displayed by <c>ToolCallBlock</c>.</item>
    /// <item><b>Status, Result, TotalTokens, ErrorMessage</b> — shown in the header.</item>
    /// </list>
    /// </summary>
    public void ReleaseMemory()
    {
        // Cancel any pending approval waiters on the sub-agent's handler.
        // This prevents a race condition where a tool call with ApprovalStatus.Pending
        // could route its approval to the wrong (parent) handler after the sub-agent
        // finishes. We keep the ToolCallHandler reference (do NOT null it) so that
        // any late-arriving approval response is handled by the correct sub-agent
        // handler rather than falling back to the parent's DI-injected handler.
        ToolCallHandler?.CancelPendingApprovals();

        // Clear the pending tool call ID — only relevant during execution.
        PendingToolCallId = null;

        // Atomically take the CancellationTokenSource. If Cancel() already took it
        // (user clicked cancel), Exchange returns null and disposal is skipped —
        // Cancel() already disposed it. This prevents ObjectDisposedException when
        // Cancel() and ReleaseMemory() race on different threads.
        var cts = Interlocked.Exchange(ref _cancelSource, null);
        cts?.Dispose();

        // The loop has exited — no one waits on the pause gate anymore.
        _pauseGate.Dispose();

        // Clean up heavy per-message transient data.
        // Segments are used only by MessageContent.razor (main chat), not by SubAgentView.
        // SubAgentView renders Content/ReasoningContent/ToolCalls directly.
        ForEachMessage(msg =>
        {
            msg.Segments.Clear();
            msg.IsShouldRender = false;
            msg.IsStreaming = false;
            msg.TempContent = string.Empty;
            msg.DisplayContent = null;
            msg.PlanContent = null;
        });
    }
}
