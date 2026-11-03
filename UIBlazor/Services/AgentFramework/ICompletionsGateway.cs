namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Unified contract for LLM completions gateways. Implementations: the tool-invocation
/// loop (main agent, sub-agent branches) and the single-shot IChatClient adapter (compression).
/// All call state (model, usage, tool_calls, finish_reason, error) is written exclusively to
/// <paramref name="resultCapture"/> / <paramref name="targetSession"/>, never to instance properties -
/// full isolation between main agent, sub-agents and compression.
/// </summary>
internal interface ICompletionsGateway
{
    /// <summary>
    /// Streams completion deltas for the given wire messages.
    /// </summary>
    /// <param name="messages">Wire-shaped messages (<see cref="WireMessage"/> from the session).</param>
    /// <param name="tools">Resolved native tool definitions; null or empty means no tools.</param>
    /// <param name="targetSession">Session to which usage/token counters are written (sub-agent support).</param>
    /// <param name="resultCapture">Per-call state accumulator.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        IReadOnlyList<WireMessage> messages,
        IEnumerable<Tool>? tools,
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        CancellationToken cancellationToken);
}
