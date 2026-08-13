namespace UIBlazor.Services.Interfaces;

public interface IToolCallHandler
{
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
