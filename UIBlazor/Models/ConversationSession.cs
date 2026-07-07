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
            TotalTokens -= message.Timings?.Tokens ?? 0 + message.ToolCalls?.Sum(t => t.Tokens) ?? 0;
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

        messages.AddRange(PrepareMessages(Messages));

        return Messages is [.., { IsStreaming: true }] // не отправлять последнее сообщение, если оно стримится
            ? messages.SkipLast(1)
            : messages;
    }

    private IEnumerable<object> PrepareMessages(IEnumerable<VisualChatMessage> Input)
    {
        var messages = new List<object>();

        foreach (var message in Input)
        {
            if (message.Role == ChatMessageRole.Assistant && message.ToolCalls is { Count: > 0 })
            {
                // Assistant message with native tool_calls
                var toolCalls = message.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = tc.Type,
                    function = new { name = tc.Function.Name, arguments = tc.Function.Arguments }
                }).ToList();

                messages.Add(new
                {
                    role = ChatMessageRole.Assistant,
                    content = string.IsNullOrEmpty(message.Content) ? null : message.Content,
                    tool_calls = toolCalls
                });

                // Tool results stored nested must be sent as separate messages to the LLM
                foreach (var toolCall in message.ToolCalls.Where(c => c.Result is not null && !string.IsNullOrEmpty(c.Id)))
                {
                    messages.Add(new
                    {
                        role = ChatMessageRole.Tool,
                        tool_call_id = toolCall.Id,
                        content = toolCall.Result!.Content
                    });
                }
            }
            else
            {
                messages.Add(new { role = message.Role, content = message.Content });
            }
        }

        return messages;
    }

    public (IEnumerable<object> Messages, VisualChatMessage? LastUserMessage) GetFormattedMessagesForCompress()
    {
        var messages = new List<object>
        {
            // Add system message
            new {
                role = ChatMessageRole.System,
                content = """
                        You are an anchored context summarization assistant for coding sessions.
                        Summarize only the conversation history you are given. The newest turns may be kept verbatim outside your summary, so focus on the older context that still matters for continuing the work.
                        Always follow the exact output structure requested by the user prompt. Keep every section, preserve exact file paths and identifiers when known, and prefer terse bullets over paragraphs.
                        Do not answer the conversation itself. Do not mention that you are summarizing, compacting, or merging context. Respond in the same language as the conversation.
                        """
            }
        };

        var lastUserMessage = Messages.TakeLast(2).FirstOrDefault(m => m.Role == ChatMessageRole.User);
        var compressedMessages = Messages.SkipLast(lastUserMessage is null ? 1 : 2);

        messages.AddRange(PrepareMessages(compressedMessages));

        messages.Add(new
        {
            role = ChatMessageRole.User,
            content = """
                Create a new anchored summary from the conversation history.

                Output exactly the Markdown structure shown inside <template> and keep the section order unchanged. Do not include the <template> tags in your response.
                <template>
                ## Goal
                - [single-sentence task summary]

                ## Constraints & Preferences
                - [user constraints, preferences, specs, or "(none)"]

                ## Progress
                ### Done
                - [completed work or "(none)"]

                ### In Progress
                - [current work or "(none)"]

                ### Blocked
                - [blockers or "(none)"]

                ## Key Decisions
                - [decision and why, or "(none)"]

                ## Next Steps
                - [ordered next actions or "(none)"]

                ## Critical Context
                - [important technical facts, errors, open questions, or "(none)"]

                ## Relevant Files
                - [file or directory path: why it matters, or "(none)"]
                </template>

                Rules:
                - Keep every section, even when empty.
                - Use terse bullets, not prose paragraphs.
                - Preserve exact file paths, commands, error strings, and identifiers when known.
                - Do not mention the summary process or that context was compacted.
                """
        });

        return (messages, lastUserMessage);
    }
}

