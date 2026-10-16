namespace UIBlazor.Models;

/// <summary>
/// Wire-format message of an LLM request (Chat Completions DTO shape), produced by
/// <see cref="ConversationSession.GetFormattedMessages"/> and consumed by
/// AfMessageMapper → Microsoft.Extensions.AI ChatMessage. Typed DTO instead of the
/// legacy anonymous objects removes the serialize/deserialize roundtrip of the whole
/// conversation history on every request.
/// </summary>
public sealed class WireMessage
{
    public string Role { get; init; } = ChatMessageRole.User;

    public string? Content { get; init; }

    /// <summary>For <see cref="ChatMessageRole.Tool"/>: the tool call this result answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For <see cref="ChatMessageRole.Assistant"/>: tool calls made by this turn.</summary>
    public IReadOnlyList<WireToolCall>? ToolCalls { get; init; }
}

/// <summary>
/// Wire-format tool call: the arguments stay a raw JSON string, same as on the wire.
/// </summary>
public sealed record WireToolCall(string Id, string Name, string? Arguments);
