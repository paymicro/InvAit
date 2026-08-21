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
                return [.._messages];
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
    /// </summary>
    public void AddMessage(VisualChatMessage message)
    {
        lock (_messagesLock)
            _messages.Add(message);
    }

    /// <summary>
    /// Thread-safe: removes a message from the sub-agent conversation.
    /// </summary>
    public void RemoveMessage(VisualChatMessage message)
    {
        lock (_messagesLock)
            _messages.Remove(message);
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
            return [.._messages];
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
    /// The sub-agent's own ToolCallHandler for processing tool call approvals.
    /// Isolated from the main agent's ToolCallHandler to prevent approval waiter conflicts.
    /// UI components use this to route approval responses for sub-agent tool calls.
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
}
