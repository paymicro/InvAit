using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Single shared converter from streaming updates into UI deltas - used by the single-shot
/// gateway (<see cref="AfCompletionsAdapter"/>), which is now the only LLM seam.
/// Responsibilities per update:
/// - text/reasoning extraction incl. Kimi/DeepSeek inline think blocks;
/// - per-call tool-call accumulation (a new <c>MessageId</c> inside ONE HTTP response means a new
///   assistant message) exposed through <see cref="CompletionsResult.AccumulatedToolCalls"/>;
/// - usage capture and heuristic token estimation (legacy parity: ~4 chars per token)
///   rebased from the pre-run base <paramref name="estimateBase"/> (re-based again on every
///   in-call message boundary together with the per-turn counter reset);
/// - legacy finish_reason fallback applied by <see cref="FinalizeStream"/>﻿ after the stream ends.
/// Turn boundaries BETWEEN LLM calls are NOT signalled here: the caller owns the turn cycle
/// and calls the gateway once per turn.
/// Per-call state lives in the mapper: never wire it into instance fields of callers.
/// </summary>
internal sealed class AfDeltaMapper(ConversationSession targetSession, CompletionsResult capture, int estimateBase)
{
    private List<ToolCall> _turnAcc = [];
    private string? _lastMessageId;
    private bool _isFirst = true;
    private bool _isStart = true; // think block may only open in the first chunk of a turn
    private bool _inThink;

    // Last-seen accumulated arguments length per call id: the progressive stream re-delivers
    // the FULL accumulated arguments on every chunk, so the token estimate must only count
    // the per-chunk increment (summing the whole payload again would over-count quadratically).
    private readonly Dictionary<string, int> _lastArgsLength = [];

    /// <summary>
    /// The run pump: creates the mapper rebased from the pre-run session token base, clears
    /// the per-run tool-call accumulator and yields every update mapped into a delta.
    /// No turn-boundary marker is emitted: one gateway call is ONE turn, the caller owns the
    /// cycle.
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

        // Per-turn counters reset at the START of every LLM call: each PumpAsync invocation
        // is ONE turn (one HTTP request). The final usage chunk of the turn overwrites the
        // estimate with the provider-reported number.
        capture.ResetPerTurnCounters();

        await foreach (var raw in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var delta = mapper.Map(raw);

            if (delta is not null)
            {
                yield return delta;
            }
        }

        mapper.FinalizeStream();
    }

    /// <summary>
    /// Converts one update. Returns null when the update carries no renderable payload
    /// (e.g. usage-only or empty chunk).
    /// <para>
    /// A changed <c>MessageId</c> inside ONE HTTP response (non-streaming replay, Responses
    /// item ids, a final usage chunk) resets the per-message counters and re-bases the token
    /// estimate - but it is NOT a turn boundary for the caller: the turn cycle lives in
    /// <c>AIChat</c>, one gateway call per turn.
    /// </para>
    /// </summary>
    public ChatDelta? Map(ChatResponseUpdate update)
    {
        // New assistant message inside the SAME HTTP call: reset per-message accumulators and
        // think-block state.
        if (!string.IsNullOrEmpty(update.MessageId) && update.MessageId != _lastMessageId)
        {
            if (!_isFirst)
            {
                _turnAcc = [];
                _isStart = true;
                _inThink = false;
                // A MessageId change inside ONE HTTP call is NOT a turn boundary: only the
                // per-message accumulators reset here. capture.AccumulatedToolCalls is deliberately
                // left untouched — it keeps pointing at the finished call list of the previous
                // message, with its final arguments. Providers do change MessageId inside one
                // response (final usage chunk, Responses item ids), and clearing the capture there
                // lost calls the UI had already rendered: the turn loop saw null, finished silently
                // and never executed them ("the tool is called but nothing happens"). The per-call
                // reset belongs to PumpAsync, and the loop clears the accumulator itself right
                // before executing.
                capture.CompletionTokens = 0;
                capture.ReasoningTokens = 0;
                capture.Usage = null;
                // The token estimate re-bases from the CURRENT session total (the previous
                // message's visible tokens included — that is its correct context position);
                // the final usage chunk, when it comes, overwrites capture.Usage anyway.
                estimateBase = targetSession.TotalTokens;
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

        var toolCallChars = 0;
        if (hadToolCall && _turnAcc.Count > 0)
        {
            capture.AccumulatedToolCalls = _turnAcc;
            delta.ToolCalls = _turnAcc;

            // Per-chunk increment of the accumulated arguments (last-wins growth),
            // not the whole payload — see _lastArgsLength.
            foreach (var tc in _turnAcc)
            {
                var prev = _lastArgsLength.TryGetValue(tc.Id, out var len) ? len : 0;
                if (tc.Function.Arguments.Length > prev)
                {
                    toolCallChars += tc.Function.Arguments.Length - prev;
                    _lastArgsLength[tc.Id] = tc.Function.Arguments.Length;
                }
            }
        }

        // heuristic token estimate until Usage arrives: always rebased from the pre-run base
        if (capture.Usage is null)
        {
            AfUsageHelpers.EstimateTokens(delta, toolCallChars, capture, targetSession, estimateBase);
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
