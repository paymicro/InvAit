using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Shared delta-processing helpers for the Agent Framework paths (single-shot adapter
/// and the tool-invocation loop): tool-call accumulation, usage capture, heuristic token
/// estimation and inline think-block handling (Kimi / DeepSeek R1 style).
/// </summary>
internal static class AfUsageHelpers
{
    internal const string ThinkStart = "<think>";
    internal const string ThinkEnd = "</think>";

    /// <summary>
    /// Records usage data from a response. Session.TotalTokens keeps the context-relevant
    /// size: reasoning tokens are excluded because they are not resent in subsequent requests.
    /// </summary>
    public static void CaptureUsage(
        UsageDetails details, CompletionsResult resultCapture, ConversationSession targetSession)
    {
        if (details.TotalTokenCount is null && details.OutputTokenCount is null && details.InputTokenCount is null)
        {
            return;
        }

        var usageInfo = new UsageInfo
        {
            PromptTokens = (int)(details.InputTokenCount ?? 0),
            CompletionTokens = (int)(details.OutputTokenCount ?? 0),
            TotalTokens = (int)(details.TotalTokenCount ?? Math.Max(0, (details.InputTokenCount ?? 0) + (details.OutputTokenCount ?? 0)))
        };

        if (details.ReasoningTokenCount is { } reasoningTokens)
        {
            usageInfo.CompletionDetails = new CompletionTokensDetails { ReasoningTokens = (int)reasoningTokens };
            resultCapture.ReasoningTokens = usageInfo.ReasoningTokens;
        }

        resultCapture.Usage = usageInfo;
        resultCapture.CompletionTokens = usageInfo.CompletionTokens;

        // reasoning tokens are not resent - exclude them from the context position
        targetSession.TotalTokens = usageInfo.TotalTokens - resultCapture.ReasoningTokens;
    }

    /// <summary>
    /// Approximate token count while streaming, before usage data arrives (~4 chars per token).
    /// Callers must pass the SAME pre-run base on every delta: visible tokens accumulate inside
    /// resultCapture, so the resulting session TotalTokens is cumulative from the base.
    /// <paramref name="toolCallChars"/> is the PER-CHUNK INCREMENT of tool-call argument chars
    /// (not the whole accumulated payload — the progressive stream re-delivers the full
    /// accumulated arguments on every chunk, and summing them again would over-count quadratically).
    /// </summary>
    public static void EstimateTokens(
        ChatDelta delta, int toolCallChars, CompletionsResult resultCapture, ConversationSession targetSession, int sessionTokens)
    {
        var contentChars = (delta.Content?.Length ?? 0) + toolCallChars;
        var reasoningChars = delta.ReasoningContent?.Length ?? 0;
        var estimatedChars = contentChars + reasoningChars;

        if (estimatedChars == 0)
        {
            return;
        }

        resultCapture.CompletionTokens += Math.Max(1, estimatedChars / 4);

        if (reasoningChars > 0)
        {
            resultCapture.ReasoningTokens += Math.Max(1, reasoningChars / 4);
        }

        // reasoning is not resent - excluded from the context position immediately
        targetSession.TotalTokens = sessionTokens + resultCapture.VisibleCompletionTokens;
    }

    /// <summary>
    /// Accumulates one streaming FunctionCallContent into the per-turn tool-call list.
    /// Returns the (possibly created) list, or the original on a malformed entry.
    /// </summary>
    public static List<ToolCall> AccumulateToolCall(FunctionCallContent fcc, List<ToolCall> turnAcc)
    {
        // Only the call id is mandatory: continuation pieces may carry no name (the name
        // arrived with the first piece or lands in a later one) — dropping them would
        // truncate the accumulated arguments.
        if (string.IsNullOrEmpty(fcc.CallId))
        {
            return turnAcc;
        }

        var existing = turnAcc.FirstOrDefault(tc => tc.Id == fcc.CallId);
        if (existing is null)
        {
            existing = new ToolCall
            {
                Index = turnAcc.Count,
                Id = fcc.CallId,
                Type = "function",
                Function = new ToolCallFunction { Name = fcc.Name ?? string.Empty }
            };
            turnAcc.Add(existing);
        }
        else if (!string.IsNullOrEmpty(fcc.Name) && string.IsNullOrEmpty(existing.Function.Name))
        {
            // The name landed in a later piece (some servers send it after the first chunk).
            existing.Function.Name = fcc.Name;
        }

        if (fcc.Arguments is { Count: > 0 })
        {
            existing.Function.Arguments = JsonSerializer.Serialize(fcc.Arguments);
        }

        return turnAcc;
    }

    public static string? Combine(string? first, string? second)
        => first is null ? second : second is null ? first : first + second;

    /// <summary>
    /// Kimi/DeepSeek style: a think block inside Content is returned as (Reasoning, Content-or-null).
    /// </summary>
    public static ThinkResult ProcessThinkBlocks(string content, ref bool isStart, ref bool isReasoningContent)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new(null, null);
        }

        if (!isReasoningContent)
        {
            if (isStart && content.StartsWith(ThinkStart))
            {
                // thinking can only start in the first chunk of the turn
                isReasoningContent = true;
                return new(content.Replace(ThinkStart, string.Empty), null);
            }

            return new(null, content);
        }

        // inside the think block
        if (content.Contains(ThinkEnd))
        {
            // finished thinking: the rest of the chunk goes to Content (relevant for Kimi2)
            isReasoningContent = false;
            return new(null, content.Replace(ThinkEnd, string.Empty));
        }

        return new(content, null);
    }

    public readonly record struct ThinkResult(string? Reasoning, string? Content);
}
