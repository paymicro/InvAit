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
    public int TotalTokens { get; set => SetIfChanged(ref field, Math.Clamp(value, 0, int.MaxValue)); } = 0;

    /// <summary>
    /// Gets or sets the current application mode for this session.
    /// </summary>
    public AppMode Mode
    {
        get => field;
        set
        {
            var previous = field;
            SetIfChanged(ref field, value);
            if (!EqualityComparer<AppMode>.Default.Equals(previous, value))
                MarkChanged();
        }
    } = AppMode.Chat;

    /// <summary>
    /// Raised on every STRUCTURAL mutation of the session: messages added/removed/replaced,
    /// content overwritten, mode switched. Persistence (autosave) subscribes here, so saving a
    /// session is an invariant of the model instead of a duty of every caller — a new code path
    /// that mutates history cannot forget to save.
    /// <para>
    /// Deliberately NOT raised for <see cref="TotalTokens"/>: the token counter is updated on
    /// every streamed delta, so it would turn autosave into a per-token writer. It rides along
    /// with the next structural change instead.
    /// </para>
    /// </summary>
    internal event Action<ConversationSession>? Changed;

    private void MarkChanged() => Changed?.Invoke(this);

    /// <summary>Monotonic version of the history content. Bumped by every structural or content
    /// mutation so a long-running agent run can detect that the history changed under it without
    /// comparing snapshots by index.</summary>
    [JsonIgnore]
    public long Revision { get; private set; }

    /// <summary>Bumps Revision. Never serialized: a reloaded session starts a fresh run.</summary>
    public void BumpRevision() => Revision++;

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

        BumpRevision();
        MarkChanged();
    }

    /// <summary>
    /// Removes a message from the conversation.
    /// Thread-safe.
    /// </summary>
    public void RemoveMessage(string id)
    {
        var removed = false;
        lock (_messagesLock)
        {
            var message = Messages.FirstOrDefault(m => m.Id == id);
            if (message != null)
            {
                TotalTokens -= (message.Timings?.Tokens ?? 0) + (message.ToolCalls?.Sum(t => t.Tokens) ?? 0);
                Messages.Remove(message);
                LastUpdated = DateTime.Now;
                removed = true;
            }
        }

        if (removed)
        {
            BumpRevision();
            MarkChanged();
        }
    }

    /// <summary>
    /// Removes the specified message object from the conversation.
    /// Thread-safe.
    /// </summary>
    public void RemoveMessage(VisualChatMessage message)
    {
        var removed = false;
        lock (_messagesLock)
        {
            if (Messages.Remove(message))
            {
                TotalTokens -= (message.Timings?.Tokens ?? 0) + (message.ToolCalls?.Sum(t => t.Tokens) ?? 0);
                LastUpdated = DateTime.Now;
                removed = true;
            }
        }

        if (removed)
        {
            BumpRevision();
            MarkChanged();
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
    /// Raised after <see cref="SetMessages"/> replaces the whole history.
    /// Attached mirrors (<see cref="SubAgentMessage"/>) listen to stay in sync:
    /// context compression rewrites the session directly via SetMessages,
    /// bypassing the SubAgentMessage wrapper methods.
    /// </summary>
    internal event Action<List<VisualChatMessage>>? MessagesReplaced;

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

        // Outside the lock: subscribers update their own mirrors and must not
        // re-enter SetMessages (the recursion guard is on the subscriber side).
        MessagesReplaced?.Invoke(messages);

        // One bump per operation: SetMessages replaces the whole list at once (a per-message
        // bump inside the lock would report an intermediate history nobody can observe).
        BumpRevision();
        MarkChanged();
    }

    /// <summary>
    /// Replaces the collapsed part of the history with ONE summary message, IN PLACE:
    /// only the given objects are removed (compared by reference), the summary takes the
    /// position of the first removed message, and every other message keeps its identity
    /// and order.
    /// <para>
    /// Unlike <see cref="SetMessages"/> this never drops messages appended while the
    /// summary was generated (user interjects) and never orphans the live streaming message
    /// of the run that triggered the compression — the agent loop keeps writing into that
    /// exact object, so removing it froze the visible generation.
    /// </para>
    /// </summary>
    /// <param name="toCollapse">Messages to remove (reference match only).</param>
    /// <param name="summary">Message that replaces them.</param>
    /// <returns>False when none of the messages is in the history (nothing changed).</returns>
    public bool CollapseMessages(IReadOnlyList<VisualChatMessage> toCollapse, VisualChatMessage summary)
    {
        if (toCollapse.Count == 0)
            return false;

        var removed = false;
        lock (_messagesLock)
        {
            var insertAt = int.MaxValue;
            foreach (var message in toCollapse)
            {
                var index = Messages.FindIndex(m => ReferenceEquals(m, message));
                if (index < 0)
                    continue;

                if (index < insertAt)
                    insertAt = index;

                Messages.RemoveAt(index);
                removed = true;
            }

            if (!removed)
                return false;

            Messages.Insert(Math.Min(insertAt, Messages.Count), summary);
            LastUpdated = DateTime.Now;
        }

        // Outside the lock: subscribers (SubAgentMessage mirrors) update their own copies and
        // must not re-enter the session.
        MessagesReplaced?.Invoke(GetMessagesSnapshot());

        // One bump per operation: the whole collapse (N removals + 1 insert) is a single
        // observable history mutation.
        BumpRevision();
        MarkChanged();
        return true;
    }

    /// <summary>
    /// Removes the EMPTY assistant bubbles of a session loaded from storage.
    /// <para>
    /// Older builds created an assistant message eagerly at every turn boundary and, when no
    /// payload arrived, left it in the history; turns are created lazily today (the bubble enters
    /// the history on the first payload), so nothing recreates them — but old sessions still carry
    /// them, and a reloaded session must neither render nor send such a bubble.
    /// </para>
    /// <para>
    /// The heuristic is content-based on purpose: an assistant turn with no text, no reasoning and
    /// no tool calls carries nothing the model could use. Message identity is preserved for every
    /// message that survives.
    /// </para>
    /// </summary>
    /// <returns>Number of removed messages (0 for an already clean session).</returns>
    public int PurgeLegacyEmptyTurns()
    {
        List<VisualChatMessage> removed;
        lock (_messagesLock)
        {
            removed = Messages
                .Where(IsEmptyAssistant)
                .ToList();

            if (removed.Count == 0)
                return 0;

            foreach (var message in removed)
                Messages.Remove(message);
        }

        // One bump for the whole operation: callers see a single history mutation.
        BumpRevision();
        MarkChanged();
        return removed.Count;
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
        var updated = false;
        lock (_messagesLock)
        {
            var message = Messages.FirstOrDefault(m => m.Id == id);
            if (message != null)
            {
                message.Content = content;
                LastUpdated = DateTime.Now;
                updated = true;
            }
        }

        if (updated)
        {
            BumpRevision();
            MarkChanged();
        }
    }

    /// <summary>Result content attached to a tool call that never produced one.</summary>
    private const string OrphanToolCallStubContent =
        "The tool call was not executed (run interrupted before the result was produced).";

    /// <summary>Single validated entry point for building the LLM request history.
    /// Guarantees the shape the API accepts:
    ///  - no orphaned tool_calls (every ToolCall without Result gets a stub result, otherwise
    ///    the provider rejects assistant(tool_calls) without matching tool messages);
    ///  - no empty assistant messages (Content blank, no ReasoningContent, no ToolCalls);
    ///  - streaming messages never leak into the request, wherever they sit in the list;
    ///  - duplicate message ids are dropped (first wins).
    /// </summary>
    /// <param name="systemPrompt">The system prompt to include.</param>
    /// <param name="contentFilter">Optional filter applied to tool result content.</param>
    /// <param name="live">Message the caller is still writing into; excluded by reference.</param>
    /// <returns>
    /// The wire history (system prompt first) and the last user message that survived
    /// normalization (<c>null</c> when the history has no user turn).
    /// </returns>
    public (List<WireMessage> Messages, VisualChatMessage? LastUserMessage) BuildWire(
        string systemPrompt,
        IContentFilter? contentFilter = null,
        VisualChatMessage? live = null)
    {
        // Normalisation runs on the SNAPSHOT, before formatting: PrepareMessages emits a tool
        // message only for a call that already carries a Result, so stubbing after the wire was
        // built would be too late (the assistant(tool_calls) would already be unanswered).
        var visible = CollectVisibleForWire(live);

        var messages = new List<WireMessage>(visible.Count + 1)
        {
            // Add system message
            new() { Role = ChatMessageRole.System, Content = systemPrompt }
        };

        messages.AddRange(PrepareMessages(visible, contentFilter));

        return (messages, visible.LastOrDefault(m => m.Role == ChatMessageRole.User));
    }

    /// <summary>
    /// Builds the validated snapshot <see cref="BuildWire"/> formats: live/streaming/empty/
    /// duplicate messages removed, orphan tool calls stubbed. Thread-safe (snapshot under lock).
    /// </summary>
    private List<VisualChatMessage> CollectVisibleForWire(VisualChatMessage? live)
    {
        List<VisualChatMessage> snapshot;
        lock (_messagesLock)
            snapshot = [.. Messages];

        // Дедуп по identity, а НЕ по Id: VisualChatMessage.Id помечен [JsonIgnore] и генерируется
        // в конструкторе, поэтому у загруженных из JSON сообщений Id всегда новый — по нему дубликат
        // не поймать. Реальный дубликат в этом коде — ОДИН И ТОТ ЖЕ объект, попавший в список дважды
        // (повторный attach зеркала SubAgentMessage, пересекающиеся snapshot'ы SetMessages).
        // Первое вхождение выигрывает.
        var seen = new HashSet<VisualChatMessage>(ReferenceEqualityComparer.Instance);
        var visible = new List<VisualChatMessage>(snapshot.Count);

        foreach (var message in snapshot)
        {
            if (message is null)
                continue;

            // Streaming messages never leak into the request, wherever they sit in the list.
            if (message.IsStreaming)
                continue;

            // The caller's own live message is not part of its request.
            if (live is not null && ReferenceEquals(message, live))
                continue;

            // Technical notes are UI diagnostics, never context: the model must not pay for our
            // transport noise, and a "Retry 2/10" line in the request would invite it to comment.
            if (message.IsTechnical)
                continue;

            if (IsEmptyAssistant(message))
                continue;

            if (!seen.Add(message))
                continue;

            visible.Add(message);
        }

        // Orphan tool calls get a stub result. Мутируем ИСХОДНЫЕ объекты сессии (на них смотрит UI),
        // но только для сообщений, реально попавших в wire: стаб на исключённом сообщении показал бы
        // пользователю фейковое «не выполнено».
        foreach (var message in visible)
        {
            if (message.Role != ChatMessageRole.Assistant || message.ToolCalls is not { Count: > 0 } toolCalls)
                continue;

            foreach (var toolCall in toolCalls)
            {
                // Без Id tool-сообщение не сопоставить вызову — PrepareMessages такой вызов
                // не разворачивает; чинится только на уровне источника (API всегда даёт Id).
                if (toolCall.Result is not null || string.IsNullOrEmpty(toolCall.Id))
                    continue;

                toolCall.Result = new ToolResult
                {
                    Content = OrphanToolCallStubContent,
                    Name = toolCall.Function.Name,
                    Success = false
                };
            }
        }

        return visible;
    }

    /// <summary>
    /// Assistant message that carries nothing the model could use: no text, no reasoning and no
    /// tool calls. Providers reject such turns, so they never reach the request, and a session
    /// loaded from storage drops them (<see cref="PurgeLegacyEmptyTurns"/>).
    /// </summary>
    internal static bool IsEmptyAssistant(VisualChatMessage message)
        => message.Role == ChatMessageRole.Assistant
           && string.IsNullOrWhiteSpace(message.Content)
           && string.IsNullOrWhiteSpace(message.ReasoningContent)
           && message.ToolCalls is not { Count: > 0 };

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
            snapshot = [.. Messages];

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

