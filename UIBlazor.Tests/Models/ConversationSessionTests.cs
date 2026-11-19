namespace UIBlazor.Tests.Models;

/// <summary>
/// <see cref="ConversationSession.BuildWire"/> is the SINGLE assembly point of the LLM request
/// history: it filters out the live/streaming message, empty assistant turns and normalises
/// orphaned tool calls — the wire messages must carry the real conversation only.
/// </summary>
public class ConversationSessionTests
{
    [Fact]
    public void BuildWire_EmptyAssistantMessage_ExcludedFromHistory()
    {
        // Arrange - a turn that produced no payload: the bubble never enters the history
        // (turns are created lazily today; a reloaded legacy session may still carry one).
        var session = new ConversationSession { Id = "s" };
        session.AddMessage(VisualChatMessage.CreateUser("hello"));
        session.AddMessage(new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            IsEmptyTurn = true,
            Content = string.Empty
        });

        // Act
        var (messages, _) = session.BuildWire("sys");

        // Assert: system + the real user message; the empty assistant turn must not reach the LLM.
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatMessageRole.System, messages[0].Role);
        Assert.Equal("sys", messages[0].Content);
        Assert.Equal(ChatMessageRole.User, messages[1].Role);
        Assert.Equal("hello", messages[1].Content);
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Assistant);
    }

    [Fact]
    public void BuildWire_StreamingMessageWithToolResults_NoOrphanedToolCalls()
    {
        // Arrange - streaming assistant carrying tool results: PrepareMessages unfolds it into
        // SEVERAL wire messages [assistant(tool_calls), tool(r1), tool(r2)]. The old drop-by-wire-count
        // (Take(count - 1)) cut only the LAST tool message, leaving assistant(tool_calls) without
        // its answer → orphaned tool → API 400. The identity drop removes the WHOLE message.
        var session = new ConversationSession { Id = "s" };
        session.AddMessage(VisualChatMessage.CreateUser("do work"));
        session.AddMessage(new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            IsStreaming = true,
            Content = string.Empty,
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "c1",
                    Function = new ToolCallFunction { Name = "read_files", Arguments = "{\"filePath\":\"a.cs\"}" },
                    Result = new ToolResult { Content = "file data", Name = "read_files", Success = true }
                },
                new ToolCall
                {
                    Id = "c2",
                    Function = new ToolCallFunction { Name = "grep_search", Arguments = "{\"pattern\":\"x\"}" },
                    Result = new ToolResult { Content = "match", Name = "grep_search", Success = true }
                }
            ]
        });

        // Act
        var (messages, _) = session.BuildWire("sys");

        // Assert: system + user only — the streaming message is dropped WHOLE (no orphaned
        // assistant(tool_calls) and no tool messages answering a dropped call).
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatMessageRole.System, messages[0].Role);
        Assert.Equal(ChatMessageRole.User, messages[1].Role);
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Assistant);
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Tool);
    }

    [Fact]
    public void BuildWire_CompletedTailWithToolResults_KeptVerbatim()
    {
        // Arrange - the same shape but NOT streaming: the message stays in the history verbatim
        // (assistant + its tool results), proving the drop is tied to the streaming flag only.
        var session = new ConversationSession { Id = "s" };
        session.AddMessage(VisualChatMessage.CreateUser("do work"));
        session.AddMessage(new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            ToolCalls =
            [
                new ToolCall
                {
                    Id = "c1",
                    Function = new ToolCallFunction { Name = "read_files", Arguments = "{\"filePath\":\"a.cs\"}" },
                    Result = new ToolResult { Content = "file data", Name = "read_files", Success = true }
                }
            ]
        });

        // Act
        var (messages, _) = session.BuildWire("sys");

        // Assert: system + user + assistant(tool_calls) + tool(result) — nothing dropped.
        Assert.Equal(4, messages.Count);
        Assert.Equal(ChatMessageRole.Assistant, messages[2].Role);
        Assert.Equal(ChatMessageRole.Tool, messages[3].Role);
        Assert.Equal("c1", messages[3].ToolCallId);
    }

    #region CollapseMessages (in-place compression)

    /// <summary>
    /// Builds a session from the given messages, returning them so the test can address them
    /// by reference (CollapseMessages matches by identity, not by Id).
    /// </summary>
    private static (ConversationSession Session, List<VisualChatMessage> Messages) CreateSession(int count)
    {
        var session = new ConversationSession { Id = "s" };
        var messages = new List<VisualChatMessage>();
        for (var i = 0; i < count; i++)
        {
            var message = i % 2 == 0
                ? VisualChatMessage.CreateUser($"u{i}")
                : new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = $"a{i}" };
            messages.Add(message);
            session.AddMessage(message);
        }

        return (session, messages);
    }

    [Fact]
    public void CollapseMessages_ReplacesOnlyGiven_AndInsertsSummaryInPlace()
    {
        // Arrange: u0 a1 u2 a3 u4 a5 u6 a7 (8 visible messages)
        var (session, messages) = CreateSession(8);
        var summary = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "summary" };

        // Act: collapse the middle (a3, u4) — what ChatService.PlanCompression picks.
        var collapsed = session.CollapseMessages([messages[3], messages[4]], summary);

        // Assert: [u0 a1 u2][summary][a5 u6 a7] — the summary sits where the collapsed part was,
        // NOT at the end of the history (where the model would read it as the newest turn).
        Assert.True(collapsed);
        var after = session.GetMessagesSnapshot();
        Assert.Equal(7, after.Count);
        Assert.Same(messages[0], after[0]);
        Assert.Same(messages[1], after[1]);
        Assert.Same(messages[2], after[2]);
        Assert.Same(summary, after[3]);
        Assert.Same(messages[5], after[4]);
        Assert.Same(messages[6], after[5]);
        Assert.Same(messages[7], after[6]);
    }

    [Fact]
    public void CollapseMessages_KeepsLiveStreamingMessageByIdentity()
    {
        // Arrange: an agent run is streaming into the last message. The agent loop keeps writing
        // into that exact object, so the compression must not drop it from the history
        // (SetMessages did — the UI stopped updating and the generation looked frozen).
        var (session, messages) = CreateSession(7);
        var live = VisualChatMessage.CreateStreaming();
        session.AddMessage(live);
        messages.Add(live);
        var summary = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "summary" };

        // Act
        session.CollapseMessages([messages[2], messages[3]], summary);

        // Assert: the live message is still the LAST element and is the same instance.
        var after = session.GetMessagesSnapshot();
        Assert.Same(live, after[^1]);
        Assert.Contains(live, after);
    }

    [Fact]
    public void CollapseMessages_KeepsMessagesAppendedDuringCompression()
    {
        // Arrange: the summary is generated while the user types an interject into the running
        // chat. That message was appended AFTER the compression snapshot — it must survive.
        var (session, messages) = CreateSession(6);
        var interject = VisualChatMessage.CreateUser("interject");
        session.AddMessage(interject);
        var summary = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "summary" };

        // Act: collapse the planned slice only.
        session.CollapseMessages([messages[2], messages[3]], summary);

        // Assert
        var after = session.GetMessagesSnapshot();
        Assert.Same(interject, after[^1]);
        Assert.Equal(6, after.Count); // 6 + interject - 2 collapsed + summary
    }

    [Fact]
    public void CollapseMessages_RaisesMessagesReplaced_AndChanged()
    {
        // Arrange: mirrors (SubAgentMessage) and autosave listen to these events.
        var (session, messages) = CreateSession(4);
        List<VisualChatMessage>? replaced = null;
        var changed = 0;
        session.MessagesReplaced += list => replaced = list;
        session.Changed += _ => changed++;
        var summary = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "summary" };

        // Act
        session.CollapseMessages([messages[1]], summary);

        // Assert: the mirror receives the COMPACTED history, not the removed message.
        Assert.NotNull(replaced);
        Assert.Equal(4, replaced.Count);
        Assert.Contains(summary, replaced);
        Assert.DoesNotContain(messages[1], replaced);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void CollapseMessages_NothingToCollapse_ReturnsFalse_AndChangesNothing()
    {
        // Arrange
        var (session, _) = CreateSession(3);
        var changed = 0;
        session.Changed += _ => changed++;
        var summary = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "summary" };
        var stranger = VisualChatMessage.CreateUser("not in this session");

        // Act: none of the targets belongs to the history.
        var collapsed = session.CollapseMessages([stranger], summary);

        // Assert
        Assert.False(collapsed);
        Assert.Equal(3, session.GetMessageCount());
        Assert.Equal(0, changed);
    }

    #endregion
}
