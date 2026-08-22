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
    /// Resets all captured state to null. Called at the start of each GetCompletionsAsync call.
    /// </summary>
    public void Reset()
    {
        Model = null;
        Error = null;
        Usage = null;
        FinishReason = null;
        AccumulatedToolCalls = null;
    }
}
