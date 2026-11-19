namespace UIBlazor.Models;

/// <summary>
/// Represents a chat message.
/// Saved in session history
/// </summary>
public class VisualChatMessage
{
    /// <summary>
    /// Gets or sets the unique identifier for the message.
    /// </summary>
    [JsonIgnore]
    public string Id { get; } = DateTime.Now.ToString("s") + Guid.NewGuid();

    /// <summary>
    /// Gets or sets the content of the message. Without thinking block.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    [JsonIgnore]
    public List<ContentSegment> Segments { get; } = [];

    /// <summary>
    /// Gets or sets the content of the message for display in the UI.
    /// If null, Content is used.
    /// </summary>
    [JsonIgnore]
    public string? DisplayContent { get; set; }

    /// <summary>
    /// Gets or sets the content of the Reasoning message. Aka Think block.
    /// </summary>
    [JsonIgnore]
    public string ReasoningContent { get; set; } = string.Empty;

    public MessageTimings? Timings { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the message was created.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// Флаг, что что-то поменялось
    /// </summary>
    [JsonIgnore]
    public bool IsShouldRender { get; set; }

    /// <summary>
    /// Raised when a rendered field changed while this message streams.
    /// <para>
    /// The subscribing <c>ChatMessageView</c> repaints ONE bubble from it, so a token no longer
    /// requires the parent to re-render the whole transcript. With a long history that was the
    /// dominant cost: every token ran SetParameters + ShouldRender over all messages in the
    /// session, though exactly one of them changed.
    /// </para>
    /// </summary>
    public event Action? Changed;

    /// <summary>Notifies the subscribing view that a rendered field changed.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Gets or sets whether this message is currently streaming.
    /// </summary>
    [JsonIgnore]
    public bool IsStreaming { get; set; }

    /// <summary>
    /// User interject message added to a running sub-agent branch ("write to the branch
    /// while it works"). SubAgentView renders interjects as compact user bubbles while the
    /// initial task message (also Role=User) stays hidden — it duplicates the header task.
    /// Not serialized: only relevant for live branch rendering.
    /// </summary>
    [JsonIgnore]
    public bool IsInterject { get; set; }

    /// <summary>
    /// Gets or sets whether this message is currently being edited.
    /// </summary>
    [JsonIgnore]
    public bool IsEditing { get; set; }

    /// <summary>
    /// Temporary storage for content during editing.
    /// </summary>
    [JsonIgnore]
    public string TempContent { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the role associated with the message (e.g., "user", "assistant").
    /// </summary>
    public string Role { get; set; } = ChatMessageRole.User;

    /// <summary>
    /// Native tool_calls from the API response (for assistant messages using native tools).
    /// </summary>
    public List<ToolCall>? ToolCalls { get; set; }

    /// <summary>
    /// Whether the message block is expanded or collapsed.
    /// </summary>
    [JsonIgnore]
    public bool IsExpanded { get; set; }

    /// <summary>
    /// Extracted plan content if any.
    /// </summary>
    [JsonIgnore]
    public string? PlanContent { get; set; }

    /// <summary>
    /// Whether this message contains a plan.
    /// </summary>
    [JsonIgnore]
    public bool HasPlan => !string.IsNullOrEmpty(PlanContent);

    [JsonIgnore]
    public int RetryCountdown { get; set; }

    [JsonIgnore]
    public int RetryAttempt { get; set; }

    [JsonIgnore]
    public int MaxRetryAttempts { get; set; }

    [JsonIgnore]
    public bool IsRetrying => RetryCountdown > 0;

    /// <summary>
    /// A technical note about the transport, not a conversation turn: a retry that is about to
    /// happen, a rejected request, a timeout.
    /// <para>
    /// Such a note is rendered as a compact line and is <b>never sent to the model</b>
    /// (<see cref="ConversationSession.BuildWire"/> skips it, so do compression and the token
    /// estimate). The model must not pay for our transport noise, and a "timeout" line in the
    /// context invites the model to comment on it.
    /// </para>
    /// <para>
    /// Persisted with the session on purpose: after a reload the user can still see that a run
    /// spent six attempts on a flapping endpoint — exactly the context needed to explain a slow
    /// run.
    /// </para>
    /// </summary>
    public bool IsTechnical { get; set; }

    /// <summary>
    /// Creates a technical note (see <see cref="IsTechnical"/>).
    /// </summary>
    /// <param name="content">
    /// Already-formatted single-line detail (attempt numbers, reason). Rendered as plain text, never
    /// as markdown - see <see cref="IsTechnical"/>. The kind of note (a retry) is the note's TITLE
    /// in the UI, not part of this string: the content must stay stable so two notes from two
    /// attempts are comparable at a glance.
    /// </param>
    public static VisualChatMessage CreateTechnical(string content) => new()
    {
        Role = ChatMessageRole.Assistant,
        IsTechnical = true,
        IsExpanded = true,
        Content = content
    };

    /// <summary>
    /// Creates an assistant message marked as streaming (used while receiving an LLM response).
    /// </summary>
    public static VisualChatMessage CreateStreaming(string initialContent = "") => new()
    {
        Role = ChatMessageRole.Assistant,
        IsStreaming = true,
        IsExpanded = true,
        Content = initialContent
    };

    /// <summary>
    /// Creates a user message (chat input, sub-agent task, restored after compression).
    /// </summary>
    public static VisualChatMessage CreateUser(string content, bool isExpanded = true) => new()
    {
        Role = ChatMessageRole.User,
        IsExpanded = isExpanded,
        Content = content
    };
}
