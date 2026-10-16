using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Single shared converter from streaming updates into UI deltas - used by BOTH gateways
/// (<see cref="AfCompletionsAdapter"/> single-shot and the <see cref="AfAgentLoop"/> tool loop;
/// capacity for a whole multi-turn run).
/// Responsibilities per update:
/// - text/reasoning extraction incl. Kimi/DeepSeek inline think blocks;
/// - per-turn tool-call accumulation (turn = new MessageId) with output as
///   <see cref="ChatDelta.IsStartOfTurn"/> marker + <see cref="CompletionsResult.AccumulatedToolCalls"/>;
/// - usage capture and heuristic token estimation (legacy parity: ~4 chars per token)
///   rebased from the FIXED pre-run base <paramref name="estimateBase"/>;
/// - legacy finish_reason fallback applied by <see cref="FinalizeStream"/> after the stream ends.
/// Per-call state lives in the mapper: never wire it into instance fields of callers.
/// </summary>
internal sealed class AfDeltaMapper(ConversationSession targetSession, CompletionsResult capture, int estimateBase)
{
    private List<ToolCall> _turnAcc = [];
    private string? _lastMessageId;
    private bool _isFirst = true;
    private bool _isStart = true; // think block may only open in the first chunk of a turn
    private bool _inThink;

    /// <summary>
    /// Shared run pump for BOTH gateways: creates the mapper rebased from the pre-run
    /// session token base, clears the per-run tool-call accumulator and yields every update
    /// mapped into deltas (turn-start marker before a new turn's delta).
    /// Exceptions from <paramref name="updates"/> propagate to the consumer as thrown
    /// (callers wrap the stream in <see cref="AfCancelRace.GuardAsync{T}"/> for classification).
    /// </summary>
    public static async IAsyncEnumerable<ChatDelta> PumpAsync(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        ConversationSession targetSession,
        CompletionsResult capture,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var mapper = new AfDeltaMapper(targetSession, capture, targetSession.TotalTokens);
        capture.AccumulatedToolCalls = null;

        await foreach (var raw in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var delta = mapper.Map(raw, out var turnStart);
            if (turnStart)
            {
                yield return new ChatDelta { IsStartOfTurn = true };
            }

            if (delta is not null)
            {
                yield return delta;
            }
        }

        mapper.FinalizeStream();
    }

    /// <summary>
    /// Converts one update. On a new assistant turn <paramref name="turnStart"/> is set and
    /// the caller must emit the <see cref="ChatDelta.IsStartOfTurn"/> marker BEFORE yielding
    /// the returned delta. Returns null when the update carries no renderable payload
    /// (e.g. usage-only or empty chunk).
    /// </summary>
    public ChatDelta? Map(ChatResponseUpdate update, out bool turnStart)
    {
        turnStart = false;

        // New assistant turn: emit the marker (the UI swaps the streaming message),
        // reset per-turn accumulators and think-block state.
        if (!string.IsNullOrEmpty(update.MessageId) && update.MessageId != _lastMessageId)
        {
            if (!_isFirst)
            {
                turnStart = true;
                _turnAcc = [];
                _isStart = true;
                _inThink = false;
                capture.AccumulatedToolCalls = null;
            }

            _lastMessageId = update.MessageId;
            _isFirst = false;
        }

        var delta = new ChatDelta();
        string? text = null;
        string? reasoning = null;
        var hadToolCall = false;

        if (update.Role is { } role)
        {
            delta.Role = role.Value;
        }

        if (!string.IsNullOrEmpty(update.ModelId))
        {
            capture.Model ??= update.ModelId;
        }

        // wire value ("stop"/"tool_calls")
        if (update.FinishReason is { } finishReason)
        {
            capture.FinishReason = finishReason.Value;
        }

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case UsageContent usage:
                    // CaptureUsage rebases targetSession.TotalTokens from usage data.
                    AfUsageHelpers.CaptureUsage(usage.Details, capture, targetSession);
                    break;

                case FunctionCallContent fcc:
                    _turnAcc = AfUsageHelpers.AccumulateToolCall(fcc, _turnAcc);
                    hadToolCall = true;
                    break;

                case FunctionResultContent:
                    // Already surfaced via toolCall.Result (ToolCallHandler).
                    break;

                case TextReasoningContent reasoningContent when !string.IsNullOrEmpty(reasoningContent.Text):
                    reasoning = (reasoning ?? string.Empty) + reasoningContent.Text;
                    break;

                case TextContent textContent when !string.IsNullOrEmpty(textContent.Text):
                    text = (text ?? string.Empty) + textContent.Text;
                    break;
            }
        }

        var inlineThink = AfUsageHelpers.ProcessThinkBlocks(text ?? string.Empty, ref _isStart, ref _inThink);
        var reasoningText = AfUsageHelpers.Combine(reasoning, inlineThink.Reasoning);
        if (reasoningText is not null)
        {
            delta.ReasoningContent = reasoningText;
        }

        if (!string.IsNullOrEmpty(inlineThink.Content))
        {
            delta.Content = inlineThink.Content;
        }

        if (hadToolCall && _turnAcc.Count > 0)
        {
            capture.AccumulatedToolCalls = _turnAcc;
            delta.ToolCalls = _turnAcc;
        }

        // heuristic token estimate until Usage arrives: always rebased from the pre-run base
        if (capture.Usage is null)
        {
            AfUsageHelpers.EstimateTokens(delta, capture, targetSession, estimateBase);
        }

        return HasPayload(delta) ? delta : null;
    }

    /// <summary>
    /// Legacy parity: tool calls were accumulated but finish_reason never arrived
    /// (e.g. the stream was cut before the final chunk). Called once after the stream ends.
    /// </summary>
    private void FinalizeStream()
    {
        if (capture.AccumulatedToolCalls is { Count: > 0 } && capture.FinishReason is null)
        {
            capture.FinishReason = "tool_calls";
        }
    }

    private static bool HasPayload(ChatDelta delta)
        => delta.Content is not null || delta.ReasoningContent is not null || delta.ToolCalls is not null;
}
