namespace UIBlazor.Models;

using UIBlazor.Services;

public class ConversationSession : BaseOptions
{
    /// <summary>
    /// Lock object protecting <see cref="Messages"/> from concurrent access
    /// (background streaming threads vs. UI render thread).
    /// </summary>
    [JsonIgnore]
    private readonly object _messagesLock = new();

    /// <summary>
    /// Gets or sets the unique identifier for the conversation session.
    /// </summary>
    [JsonIgnore]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of messages in the conversation.
    /// The getter/setter are for JSON serialization compatibility only.
    /// For thread-safe access from application code, use the dedicated methods:
    /// <see cref="AddMessage"/>, <see cref="RemoveMessage"/>,
    /// <see cref="GetMessagesSnapshot"/>, <see cref="GetMessageCount"/>,
    /// <see cref="SetMessages"/>.
    /// The getter returns the backing list directly (for serialization);
    /// callers must NOT use it for concurrent reads — use <see cref="GetMessagesSnapshot"/>.
    /// </summary>
    [JsonInclude]
    private List<VisualChatMessage> Messages { get; set; } = [];

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
    public int TotalTokens { get; set => SetIfChanged(ref field, value); } = 0;

    /// <summary>
    /// Gets or sets the current application mode for this session.
    /// </summary>
    public AppMode Mode { get; set => SetIfChanged(ref field, value); } = AppMode.Chat;

    /// <summary>
    /// Adds a message object to the conversation and manages memory limits.
    /// Thread-safe.
    /// </summary>
    public void AddMessage(VisualChatMessage message)
    {
        lock (_messagesLock)
        {
            Messages.Add(message);
            LastUpdated = DateTime.Now;
        }
    }

    /// <summary>
    /// Removes a message from the conversation.
    /// Thread-safe.
    /// </summary>
    public void RemoveMessage(string id)
    {
        lock (_messagesLock)
        {
            var message = Messages.FirstOrDefault(m => m.Id == id);
            if (message != null)
            {
                TotalTokens -= (message.Timings?.Tokens ?? 0) + (message.ToolCalls?.Sum(t => t.Tokens) ?? 0);
                Messages.Remove(message);
                LastUpdated = DateTime.Now;
            }
        }
    }

    /// <summary>
    /// Removes the specified message object from the conversation.
    /// Thread-safe.
    /// </summary>
    public void RemoveMessage(VisualChatMessage message)
    {
        lock (_messagesLock)
        {
            if (Messages.Remove(message))
            {
                TotalTokens -= (message.Timings?.Tokens ?? 0) + (message.ToolCalls?.Sum(t => t.Tokens) ?? 0);
                LastUpdated = DateTime.Now;
            }
        }
    }

    /// <summary>
    /// Returns a snapshot copy of the messages list for safe iteration.
    /// Thread-safe.
    /// </summary>
    public List<VisualChatMessage> GetMessagesSnapshot()
    {
        lock (_messagesLock)
            return [.. Messages];
    }

    /// <summary>
    /// Returns the number of messages in the conversation.
    /// Thread-safe.
    /// </summary>
    public int GetMessageCount()
    {
        lock (_messagesLock)
            return Messages.Count;
    }

    /// <summary>
    /// Replaces all messages with the provided list.
    /// Thread-safe.
    /// </summary>
    public void SetMessages(List<VisualChatMessage> messages)
    {
        lock (_messagesLock)
        {
            Messages = messages;
            LastUpdated = DateTime.Now;
        }
    }

    /// <summary>
    /// Returns the last message matching the predicate, or null if none found.
    /// Thread-safe.
    /// </summary>
    public VisualChatMessage? GetLastOrDefaultMessage(Func<VisualChatMessage, bool> predicate)
    {
        lock (_messagesLock)
            return Messages.LastOrDefault(predicate);
    }

    /// <summary>
    /// Updates the content of a message.
    /// Thread-safe.
    /// </summary>
    public void UpdateMessage(string id, string content)
    {
        lock (_messagesLock)
        {
            var message = Messages.FirstOrDefault(m => m.Id == id);
            if (message != null)
            {
                message.Content = content;
                LastUpdated = DateTime.Now;
            }
        }
    }

    /// <summary>
    /// Gets the conversation messages formatted for the AI API.
    /// Thread-safe: takes a snapshot of <see cref="Messages"/> under lock.
    /// </summary>
    /// <param name="systemPrompt">The system prompt to include.</param>
    /// <returns>Wire-format messages for the AI API.</returns>
    public IReadOnlyList<WireMessage> GetFormattedMessages(string systemPrompt, IContentFilter? contentFilter = null)
    {
        List<VisualChatMessage> snapshot;
        lock (_messagesLock)
            snapshot = [.. Messages];

        // Empty turn-boundary messages (turn started but no payload arrived) carry no
        // content - skip them so the LLM history stays clean.
        snapshot.RemoveAll(m => m.IsEmptyTurn);

        var messages = new List<WireMessage>
        {
            // Add system message
            new() { Role = ChatMessageRole.System, Content = systemPrompt }
        };

        // не отправлять последнее сообщение, если оно стримится. Дроп по identity ИЗ SNAPSHOT
        // (до разворачивания в wire): стримящее assistant с tool results разворачивается в
        // НЕСКОЛЬКО wire-сообщений [assistant(tool_calls), tool(r1), ...] — дроп последнего
        // wire-сообщения по count оставлял бы assistant(tool_calls) без ответа → orphaned
        // tool → API 400.
        var historyMessages = snapshot is [.., { IsStreaming: true }] ? snapshot[..^1] : snapshot;

        messages.AddRange(PrepareMessages(historyMessages, contentFilter));

        return messages;
    }

    private static IEnumerable<WireMessage> PrepareMessages(IEnumerable<VisualChatMessage> Input, IContentFilter? contentFilter = null)
    {
        var messages = new List<WireMessage>();

        foreach (var message in Input)
        {
            if (message.Role == ChatMessageRole.Assistant && message.ToolCalls is { Count: > 0 })
            {
                // Assistant message with native tool_calls
                messages.Add(new WireMessage
                {
                    Role = ChatMessageRole.Assistant,
                    Content = string.IsNullOrEmpty(message.Content) ? null : message.Content,
                    ToolCalls = message.ToolCalls
                        .Select(tc => new WireToolCall(tc.Id, tc.Function.Name, tc.Function.Arguments))
                        .ToList()
                });

                // Tool results stored nested must be sent as separate messages to the LLM
                foreach (var toolCall in message.ToolCalls.Where(c => c.Result is not null && !string.IsNullOrEmpty(c.Id)))
                {
                    var toolContent = toolCall.Result!.Content;
                    if (contentFilter is not null)
                    {
                        // Filter() always formats structured FileContent JSON into
                        // human-readable text with line numbers, even when filtering is disabled.
                        var filtered = contentFilter.Filter(toolContent, toolCall.Function.Name);
                        if (!string.IsNullOrEmpty(filtered))
                            toolContent = filtered;
                    }
                    else
                    {
                        // No filter available — format structured content with line numbers
                        toolContent = FileContentFormatter.FormatContent(toolContent);
                    }
                    messages.Add(new WireMessage
                    {
                        Role = ChatMessageRole.Tool,
                        ToolCallId = toolCall.Id,
                        Content = toolContent
                    });
                }
            }
            else
            {
                messages.Add(new WireMessage { Role = message.Role, Content = message.Content });
            }
        }

        return messages;
    }

    public (IReadOnlyList<WireMessage> Messages, VisualChatMessage? LastUserMessage) GetFormattedMessagesForCompress(IContentFilter? contentFilter = null)
    {
        List<VisualChatMessage> snapshot;
        lock (_messagesLock)
        {
            snapshot = [.. Messages];
            // Empty turn-boundary bubbles (eager pending messages) must not reach the
            // summarizer — same filter as GetFormattedMessages.
            snapshot.RemoveAll(m => m.IsEmptyTurn);
        }

        var messages = new List<WireMessage>
        {
            // Add system message
            new()
            {
                Role = ChatMessageRole.System,
                Content = """
                        You are an anchored context summarization assistant for coding sessions.
                        Summarize only the conversation history you are given. The newest turns may be kept verbatim outside your summary, so focus on the older context that still matters for continuing the work.
                        Always follow the exact output structure requested by the user prompt. Keep every section, preserve exact file paths and identifiers when known, and prefer terse bullets over paragraphs.
                        Do not answer the conversation itself. Do not mention that you are summarizing, compacting, or merging context. Respond in the same language as the conversation.
                        """
            }
        };

        var lastUserMessage = snapshot.TakeLast(2).FirstOrDefault(m => m.Role == ChatMessageRole.User);
        var compressedMessages = snapshot.SkipLast(lastUserMessage is null ? 1 : 2);

        messages.AddRange(PrepareMessages(compressedMessages, contentFilter));

        messages.Add(new WireMessage
        {
            Role = ChatMessageRole.User,
            Content = """
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

