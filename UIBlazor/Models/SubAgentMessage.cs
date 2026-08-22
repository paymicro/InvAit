namespace UIBlazor.Models;

/// <summary>
/// Status of a sub-agent execution.
/// </summary>
public enum SubAgentStatus
{
    Pending,
    Running,
    Completed,
    Cancelled,
    Failed
}

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
    public void AttachSession(ConversationSession session) => _session = session;

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
    /// The final result returned by the sub-agent to the main agent.
    /// </summary>
    public string Result { get; set; } = string.Empty;

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
    /// Set by <c>SubAgentExecutor</c> via <see cref="SetCancellationTokenSource"/>.
    /// </summary>
    [JsonIgnore]
    private CancellationTokenSource? _cancelSource;

    /// <summary>
    /// Sets the linked CancellationTokenSource so the sub-agent can be cancelled independently.
    /// Called by <c>SubAgentExecutor</c> after creating the linked token.
    /// </summary>
    public void SetCancellationTokenSource(CancellationTokenSource cts) => _cancelSource = cts;

    /// <summary>
    /// Cancels the sub-agent independently (without cancelling the parent chat).
    /// Safe to call multiple times — does nothing if already cancelled or not started.
    /// </summary>
    public void Cancel() => _cancelSource?.Cancel();

    /// <summary>
    /// Whether the sub-agent can currently be cancelled (i.e. it is still running).
    /// </summary>
    [JsonIgnore]
    public bool CanCancel => Status == SubAgentStatus.Running;

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

    /// <summary>
    /// Raises the StateChanged event to notify UI subscribers.
    /// Thread-safe invocation via Volatile.Read pattern.
    /// </summary>
    public void NotifyStateChanged() => Volatile.Read(ref StateChanged)?.Invoke();

    /// <summary>
    /// Releases heavy runtime resources that are no longer needed after the sub-agent
    /// has finished (Completed / Cancelled / Failed).
    ///
    /// This method is called by <c>SubAgentExecutor</c> in its <c>finally</c> block,
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

        // Dispose the CancellationTokenSource — no longer needed after execution.
        _cancelSource?.Dispose();
        _cancelSource = null;

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
