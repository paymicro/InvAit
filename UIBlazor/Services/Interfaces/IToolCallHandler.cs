namespace UIBlazor.Services.Interfaces;

public interface IToolCallHandler
{
    /// <summary>
    /// Raised when one or more tool calls require user approval or ask_user interaction.
    /// Parameter is the tool call ID that needs attention.
    /// </summary>
    event Action<string>? ApprovalRequired;

    void PrepareToolsForApprovals(List<ToolCall> toolCalls);

    /// <summary>
    /// Processes all tool calls in the message and returns results.
    /// </summary>
    Task ProcessToolCallsAsync(List<ToolCall> toolCalls, CancellationToken cancellationToken);

    /// <summary>
    /// Handles approval response from user.
    /// </summary>
    Task HandleApprovalAsync(string segmentId, bool approved);

    /// <summary>
    /// Handles user answer for ask_user tool.
    /// </summary>
    Task HandleAskUserAnswerAsync(string toolCallId, string answer);
}
