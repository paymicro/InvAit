namespace UIBlazor.Models;

public class ConversationSession
{
    /// <summary>
    /// Gets or sets the unique identifier for the conversation session.
    /// </summary>
    [JsonIgnore]
    public string Id { get; set; } = string.Empty;

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
    /// Gets or sets the total tokens used in the conversation.
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// Gets or sets the current application mode for this session.
    /// </summary>
    public AppMode Mode { get; set; } = AppMode.Chat;

    /// <summary>
    /// Adds a message to the conversation and manages memory limits.
    /// </summary>
    /// <param name="role">The role of the message sender.</param>
    /// <param name="content">The message content.</param>
    public void AddMessage(string role, string content)
    {
        AddMessage(new VisualChatMessage
        {
            Role = role,
            Content = content,
            Timestamp = DateTime.Now
        });
    }

    /// <summary>
    /// Adds a message object to the conversation and manages memory limits.
    /// </summary>
    public void AddMessage(VisualChatMessage message)
    {
        Messages.Add(message);
        LastUpdated = DateTime.Now;

        // Remove oldest messages if we exceed the limit
        while (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(0);
        }
    }

    /// <summary>
    /// Removes a message from the conversation.
    /// </summary>
    public void RemoveMessage(string id)
    {
        var message = Messages.FirstOrDefault(m => m.Id == id);
        if (message != null)
        {
            Messages.Remove(message);
            LastUpdated = DateTime.Now;
        }
    }

    /// <summary>
    /// Updates the content of a message.
    /// </summary>
    public void UpdateMessage(string id, string content)
    {
        var message = Messages.FirstOrDefault(m => m.Id == id);
        if (message != null)
        {
            message.Content = content;
            LastUpdated = DateTime.Now;
        }
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

