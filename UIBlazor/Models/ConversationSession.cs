namespace UIBlazor.Models;

public class ConversationSession : BaseOptions
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
    /// Gets or sets the total tokens used in the conversation.
    /// </summary>
    public int TotalTokens { get; set; }

    /// <summary>
    /// Gets or sets the current application mode for this session.
    /// </summary>
    public AppMode Mode { get; set => SetIfChanged(ref field, value); } = AppMode.Chat;

    /// <summary>
    /// Adds a message object to the conversation and manages memory limits.
    /// </summary>
    public void AddMessage(VisualChatMessage message)
    {
        Messages.Add(message);
        LastUpdated = DateTime.Now;
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
    public IEnumerable<object> GetFormattedMessages(string systemPrompt)
    {
        var messages = new List<object>
        {
            // Add system message
            new { role = ChatMessageRole.System, content = systemPrompt }
        };

        // Add conversation messages, flattening nested tool messages
        foreach (var message in Messages)
        {
            messages.Add(new { role = message.Role, content = message.Content });

            // Tool results stored nested must be sent as separate messages to the LLM
            foreach (var toolMessage in message.ToolResults)
            {
                messages.Add(new { role = ChatMessageRole.User, content = toolMessage.Content });
            }
        }

        return Messages is [.., { IsStreaming: true }] // не отправлять последнее сообщение, если оно стримится
            ? messages.SkipLast(1)
            : messages;
    }

    public (IEnumerable<object> Messages, VisualChatMessage? LastUserMessage) GetFormattedMessagesForCompress(string systemPrompt)
    {
        var messages = new List<object>
        {
            // Add system message
            new { role = ChatMessageRole.System, content = systemPrompt }
        };

        var lastUserMessage = Messages.TakeLast(2).FirstOrDefault(m => m.Role == ChatMessageRole.User);
        var compressedMessages = Messages.SkipLast(lastUserMessage is null ? 1 : 2);

        // Add conversation messages, flattening nested tool messages
        foreach (var message in compressedMessages)
        {
            messages.Add(new { role = message.Role, content = message.Content });

            // Tool results stored nested must be sent as separate messages to the LLM
            foreach (var toolMessage in message.ToolResults)
            {
                messages.Add(new { role = ChatMessageRole.User, content = toolMessage.Content });
            }
        }

        messages.Add(new
        {
            role = ChatMessageRole.User,
            content = """
                Act as a Context Compressor.
                Summarize the following tool calls and assistant responses.
                Preserve:
                    1. Final results of searches
                    2. Key variable names/file paths
                    3. Errors encountered. Remove: redundant JSON structures and intermediate steps.
                Format: 'Previous actions: [Concise Summary]'
                """
        });

        return (messages, lastUserMessage);
    }
}

