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
    /// List of tool names explicitly denied to the sub-agent.
    /// </summary>
    public string[]? DeniedTools { get; set; }

    /// <summary>
    /// The conversation messages of the sub-agent (for UI display).
    /// </summary>
    [JsonIgnore]
    public List<VisualChatMessage> Messages { get; set; } = [];

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
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raises the StateChanged event to notify UI subscribers.
    /// </summary>
    public void NotifyStateChanged() => StateChanged?.Invoke();
}
