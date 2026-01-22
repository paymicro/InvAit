using System.Text.Json.Serialization;

namespace UIBlazor.Models;

public class ConversationSession
{
    /// <summary>
    /// Gets or sets the unique identifier for the conversation session.
    /// </summary>
    [JsonIgnore]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the list of messages in the conversation.
    /// </summary>
    public List<VisualChatMessage> Messages { get; set; } = [];

    /// <summary>
    /// Gets or sets the timestamp when the conversation was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Gets or sets the timestamp when the conversation was last updated.
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.Now;

    /// <summary>
    /// Gets or sets the maximum number of messages to keep in memory.
    /// </summary>
    [JsonIgnore]
    public int MaxMessages { get; set; } = 50;

    /// <summary>
    /// Adds a message to the conversation and manages memory limits.
    /// </summary>
    /// <param name="role">The role of the message sender.</param>
    /// <param name="content">The message content.</param>
    public void AddMessage(string role, string content)
    {
        Messages.Add(new VisualChatMessage
        {
            Role = role,
            Content = content,
            Timestamp = DateTime.Now
        });

        LastUpdated = DateTime.Now;

        // Remove oldest messages if we exceed the limit
        while (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(0);
        }
    }

    /// <summary>
    /// Clears all messages from the conversation.
    /// </summary>
    public void Clear()
    {
        Messages.Clear();
        LastUpdated = DateTime.Now;
    }

    /// <summary>
    /// Gets the conversation messages formatted for the AI API.
    /// </summary>
    /// <param name="systemPrompt">The system prompt to include.</param>
    /// <returns>A list of message objects for the AI API.</returns>
    public List<object> GetFormattedMessages(string systemPrompt)
    {
        var messages = new List<object>
        {
            // Add system message
            new { role = "system", content = systemPrompt }
        };

        // Add conversation messages
        foreach (var message in Messages)
        {
            messages.Add(new { role = message.Role, content = message.Content });
        }

        return messages;
    }
}

