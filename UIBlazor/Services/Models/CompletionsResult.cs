namespace UIBlazor.Services.Models;

/// <summary>
/// Captured state from a single GetCompletionsAsync call.
/// Replaces former shared instance properties (LastCompletionsModel, LastUsage, etc.)
/// on ChatService. Each caller (main agent, sub-agent, compression) creates its own
/// instance, ensuring complete isolation between concurrent or nested LLM calls.
/// </summary>
public sealed class CompletionsResult
{
    public string? Model { get; set; }
    public string? Error { get; set; }
    public UsageInfo? Usage { get; set; }
    public string? FinishReason { get; set; }
    public List<ToolCall>? AccumulatedToolCalls { get; set; }

    /// <summary>
    /// Полный вывод модели: из <see cref="UsageInfo.CompletionTokens"/> или приблизительный подсчет во время стрима.
    /// Включает размышления — для места в контексте используйте <see cref="VisibleCompletionTokens"/>.
    /// </summary>
    public int CompletionTokens { get; set; }

    /// <summary>
    /// Токены размышлений. Из API (completion_tokens_details.reasoning_tokens),
    /// если провайдер их отдает, иначе приблизительный подсчет reasoning-дельт во время стрима.
    /// Размышления не отправляются повторно и место в контексте не занимают.
    /// </summary>
    public int ReasoningTokens { get; set; }

    /// <summary>
    /// Видимые токены ответа (без размышлений) — столько сообщение занимает в контексте.
    /// </summary>
    public int VisibleCompletionTokens => Math.Max(0, CompletionTokens - ReasoningTokens);

    /// <summary>
    /// Resets all captured state to null. Called at the start of each GetCompletionsAsync call.
    /// </summary>
    public void Reset()
    {
        Model = null;
        Error = null;
        Usage = null;
        FinishReason = null;
        AccumulatedToolCalls = null;
        CompletionTokens = 0;
        ReasoningTokens = 0;
    }
}
