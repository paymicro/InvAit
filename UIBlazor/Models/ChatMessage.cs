using System.Text.Json.Serialization;

namespace UIBlazor.Models;

/// <summary>
/// Represents a chat message.
/// </summary>
public class ChatMessage
{
    /// <summary>
    /// Gets or sets the unique identifier for the message.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the content of the message. Without thinking block.
    /// </summary>
    public string Content { get; set; } = string.Empty;

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

    [JsonIgnore]
    public string ToolName { get; set; }

    /// <summary>
    /// Gets or sets the role associated with the message (e.g., "user", "assistant").
    /// </summary>
    public string Role { get; set; } = ChatMessageRole.User;
}
