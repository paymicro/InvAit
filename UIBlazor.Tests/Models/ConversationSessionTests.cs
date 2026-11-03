namespace UIBlazor.Tests.Models;

/// <summary>
/// GetFormattedMessages filters empty turn-boundary messages (turn started but no payload
/// arrived) out of the LLM history — the wire messages must carry the real conversation only.
/// </summary>
public class ConversationSessionTests
{
    [Fact]
    public void GetFormattedMessages_IsEmptyTurnMessage_ExcludedFromHistory()
    {
        // Arrange
        var session = new ConversationSession { Id = "s" };
        session.AddMessage(VisualChatMessage.CreateUser("hello"));
        session.AddMessage(new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            IsEmptyTurn = true,
            Content = string.Empty
        });

        // Act
        var messages = session.GetFormattedMessages("sys");

        // Assert: system + the real user message; the empty assistant turn must not reach the LLM.
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatMessageRole.System, messages[0].Role);
        Assert.Equal("sys", messages[0].Content);
        Assert.Equal(ChatMessageRole.User, messages[1].Role);
        Assert.Equal("hello", messages[1].Content);
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Assistant);
    }

    [Fact]
    public void GetFormattedMessages_StreamingTailWithToolResults_NoOrphanedToolCalls()
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
        var messages = session.GetFormattedMessages("sys");

        // Assert: system + user only — the streaming tail is dropped WHOLE (no orphaned
        // assistant(tool_calls) and no tool messages answering a dropped call).
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatMessageRole.System, messages[0].Role);
        Assert.Equal(ChatMessageRole.User, messages[1].Role);
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Assistant);
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Tool);
    }

    [Fact]
    public void GetFormattedMessages_CompletedTailWithToolResults_KeptVerbatim()
    {
        // Arrange - the same shape but NOT streaming: the tail must stay in the history verbatim
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
        var messages = session.GetFormattedMessages("sys");

        // Assert: system + user + assistant(tool_calls) + tool(result) — nothing dropped.
        Assert.Equal(4, messages.Count);
        Assert.Equal(ChatMessageRole.Assistant, messages[2].Role);
        Assert.Equal(ChatMessageRole.Tool, messages[3].Role);
        Assert.Equal("c1", messages[3].ToolCallId);
    }
}
