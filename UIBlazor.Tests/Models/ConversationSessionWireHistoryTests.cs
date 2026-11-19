namespace UIBlazor.Tests.Models;

/// <summary>
/// Stage 1 of the wire-history refactor: <see cref="ConversationSession.Revision"/> (monotonic
/// mutation counter) and <see cref="ConversationSession.BuildWire"/> (single validated entry
/// point for the LLM request history — no orphaned tool calls, no empty assistant turns, no
/// streaming leakage, no duplicates).
/// </summary>
public class ConversationSessionWireHistoryTests
{
    private static ConversationSession CreateSession() => new() { Id = "s" };

    [Fact]
    public void WireHistory_EqualsSession_AfterAddEditDeleteCollapse()
    {
        // Arrange: user, assistant, user, assistant
        var session = CreateSession();
        var u0 = VisualChatMessage.CreateUser("first question");
        var a1 = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "first answer" };
        var u2 = VisualChatMessage.CreateUser("second question");
        var a3 = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "second answer" };

        session.AddMessage(u0);
        session.AddMessage(a1);
        session.AddMessage(u2);
        session.AddMessage(a3);

        // Act
        var (messages, lastUserMessage) = session.BuildWire("sys");

        // Assert: system + the 4 real turns, nothing else (no tool messages, no empty turns).
        Assert.Equal(5, messages.Count);
        Assert.Equal(ChatMessageRole.System, messages[0].Role);
        Assert.Equal("sys", messages[0].Content);
        Assert.Equal(
            new[] { ChatMessageRole.User, ChatMessageRole.Assistant, ChatMessageRole.User, ChatMessageRole.Assistant },
            messages.Skip(1).Select(m => m.Role));
        Assert.Equal("second question", lastUserMessage?.Content);
        Assert.Equal(4, session.Revision); // one bump per AddMessage

        // Act: edit → the wire must carry the NEW text only.
        var afterAdds = session.Revision;
        session.UpdateMessage(a1.Id, "first answer (edited)");
        Assert.Equal(afterAdds + 1, session.Revision);

        var afterEdit = session.BuildWire("sys").Messages;
        Assert.Contains(afterEdit, m => m.Content == "first answer (edited)");
        Assert.DoesNotContain(afterEdit, m => m.Content == "first answer");

        // Act: delete → the removed turn must disappear from the wire.
        var afterEditRevision = session.Revision;
        session.RemoveMessage(u2.Id);
        Assert.Equal(afterEditRevision + 1, session.Revision);

        var afterDelete = session.BuildWire("sys").Messages;
        Assert.DoesNotContain(afterDelete, m => m.Content == "second question");
        Assert.Equal(4, afterDelete.Count); // system + u0 + a1 + a3

        // Act: collapse → the summary replaces the collapsed part IN PLACE.
        var summary = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "## Summary\ncollapsed history" };
        var afterDeleteRevision = session.Revision;
        var collapsed = session.CollapseMessages([u0, a1], summary);

        // Assert: one bump per operation, not one per removed message.
        Assert.True(collapsed);
        Assert.Equal(afterDeleteRevision + 1, session.Revision);

        var afterCollapse = session.BuildWire("sys").Messages;
        Assert.Equal(3, afterCollapse.Count); // system + summary + a3
        Assert.Contains(afterCollapse, m => m.Content == summary.Content);
        Assert.DoesNotContain(afterCollapse, m => m.Content == "first question");
        Assert.Equal("second answer", afterCollapse[^1].Content);
    }

    [Fact]
    public void WireHistory_OrphanToolCalls_GetStubResult()
    {
        // Arrange: tc1 never produced a result (run interrupted), tc2 is answered.
        var session = CreateSession();
        session.AddMessage(VisualChatMessage.CreateUser("do work"));

        var answeredResult = new ToolResult { Content = "file data", Name = "read_files", Success = true };
        var orphan = new ToolCall
        {
            Id = "c1",
            Function = new ToolCallFunction { Name = "grep_search", Arguments = """{"pattern":"x"}""" }
        };
        var answered = new ToolCall
        {
            Id = "c2",
            Function = new ToolCallFunction { Name = "read_files", Arguments = """{"filePath":"a.cs"}""" },
            Result = answeredResult
        };

        session.AddMessage(new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            ToolCalls = [orphan, answered]
        });

        // Act
        var (messages, _) = session.BuildWire("sys");

        // Assert: the SESSION object got the stub (the UI renders the same instance) ...
        Assert.NotNull(orphan.Result);
        Assert.False(orphan.Result!.Success);
        Assert.False(string.IsNullOrWhiteSpace(orphan.Result.Content));
        Assert.Equal("grep_search", orphan.Result.Name);

        // ... the already-answered call is untouched ...
        Assert.Same(answeredResult, answered.Result);

        // ... and the wire answers BOTH calls, otherwise the provider rejects the request.
        var toolMessages = messages.Where(m => m.Role == ChatMessageRole.Tool).ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.Contains(toolMessages, m => m.ToolCallId == "c1");
        Assert.Contains(toolMessages, m => m.ToolCallId == "c2");
        Assert.Equal(2, Assert.Single(messages, m => m.Role == ChatMessageRole.Assistant).ToolCalls!.Count);
    }

    [Fact]
    public void WireHistory_StreamingMessageExcludedAnywhere()
    {
        // Arrange: the streaming message sits in the MIDDLE, not at the tail.
        var session = CreateSession();
        var u0 = VisualChatMessage.CreateUser("question one");
        var streaming = new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            Content = "partial answer",
            IsStreaming = true
        };
        var u1 = VisualChatMessage.CreateUser("question two");

        session.AddMessage(u0);
        session.AddMessage(streaming);
        session.AddMessage(u1);

        // Act
        var (messages, _) = session.BuildWire("sys");

        // Assert: system + the 2 user turns; the half-written answer never leaks.
        Assert.Equal(3, messages.Count);
        Assert.DoesNotContain(messages, m => m.Content == "partial answer");
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Assistant);

        // Act: the caller passes the live message explicitly, its streaming flag already cleared.
        streaming.IsStreaming = false;

        // Assert: excluded by IDENTITY, mid-list, so the newer user turn is not lost.
        var (withLive, _) = session.BuildWire("sys", live: streaming);
        Assert.Equal(3, withLive.Count);
        Assert.DoesNotContain(withLive, m => m.Content == "partial answer");
        Assert.Contains(withLive, m => m.Content == "question two");
    }

    [Fact]
    public void WireHistory_EmptyAssistantExcluded()
    {
        // Arrange: two useless assistant bubbles — empty and whitespace-only.
        var session = CreateSession();
        session.AddMessage(VisualChatMessage.CreateUser("question"));
        session.AddMessage(new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = string.Empty });
        session.AddMessage(new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "   " });

        // Act
        var (messages, _) = session.BuildWire("sys");

        // Assert
        Assert.Equal(2, messages.Count); // system + question
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Assistant);

        // Act: an assistant turn with ONLY reasoning is real model output — it must stay.
        session.AddMessage(new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            Content = string.Empty,
            ReasoningContent = "internal thinking"
        });

        var (withReasoning, _) = session.BuildWire("sys");

        // Assert
        Assert.Equal(3, withReasoning.Count);
        Assert.Contains(withReasoning, m => m.Role == ChatMessageRole.Assistant);
    }

    /// <summary>
    /// A technical note (retry trace, transport diagnostic) is a UI line, not context: the model
    /// must not pay for our transport noise, and a "Retry 2/10" line in the request would invite
    /// it to comment on the failure instead of continuing the task.
    /// </summary>
    [Fact]
    public void BuildWire_TechnicalNote_ExcludedFromHistory()
    {
        // Arrange
        var session = CreateSession();
        session.AddMessage(VisualChatMessage.CreateUser("do it"));
        session.AddMessage(VisualChatMessage.CreateTechnical("Retry 1/10 in 2s — boom"));
        session.AddMessage(new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "done" });

        // Act
        var (messages, _) = session.BuildWire("sys");

        // Assert: system + user + assistant, the note in between is dropped.
        Assert.Equal(3, messages.Count);
        Assert.DoesNotContain(messages, m => m.Content?.Contains("Retry 1/10") == true);
    }

    /// <summary>
    /// A technical note must not be mistaken for an empty assistant turn: it carries content, so
    /// the emptiness rule never sees it — the exclusion has to be its own filter, which this test
    /// pins (a note would otherwise be the only remaining reason the history grows silently).
    /// </summary>
    [Fact]
    public void BuildWire_TechnicalNoteOnly_HistoryHasNoAssistantTurn()
    {
        // Arrange
        var session = CreateSession();
        session.AddMessage(VisualChatMessage.CreateUser("hi"));
        session.AddMessage(VisualChatMessage.CreateTechnical("Request timed out after 100s"));

        // Act
        var (messages, lastUser) = session.BuildWire("sys");

        // Assert
        Assert.Equal(2, messages.Count); // system + user
        Assert.Equal("hi", lastUser?.Content);
    }
}
