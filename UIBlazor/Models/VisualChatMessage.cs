using System.Text.Json.Serialization;

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
    public string Id { get; set; } = DateTime.Now.ToString("s") + Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the content of the message. Without thinking block.
    /// </summary>
    public string Content { get; set; } = string.Empty;

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

    /// <summary>
    /// Gets or sets the timestamp when the message was created.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// Gets or sets whether this message is currently streaming.
    /// </summary>
    [JsonIgnore]
    public bool IsStreaming { get; set; }

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

    [JsonIgnore]
    public string ToolName { get; set; } = string.Empty;

    [JsonIgnore]
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the role associated with the message (e.g., "user", "assistant").
    /// </summary>
    public string Role { get; set; } = ChatMessageRole.User;

    /// <summary>
    /// Nested tool messages for assistant messages.
    /// </summary>
    [JsonIgnore]
    public List<VisualChatMessage> ToolMessages { get; set; } = [];

    /// <summary>
    /// Whether the message block is expanded or collapsed.
    /// </summary>
    [JsonIgnore]
    public bool IsExpanded { get; set; }
}
