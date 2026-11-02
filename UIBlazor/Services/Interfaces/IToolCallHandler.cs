namespace UIBlazor.Services.Interfaces;

public interface IToolCallHandler
{
    /// <summary>
    /// Raised when one or more tool calls require user approval or ask_user interaction.
    /// Parameter is the tool call ID that needs attention.
    /// </summary>
    event Action<string>? ApprovalRequired;

    /// <summary>
    /// Raised when a pending tool call no longer requires user interaction:
    /// an approval decision was made or an ask_user answer was provided.
    /// Parameter is the tool call ID that was resolved.
    /// </summary>
    event Action<string>? ApprovalResolved;

    void PrepareToolsForApprovals(List<ToolCall> toolCalls);

    /// <summary>
    /// Processes all tool calls in the message and returns results.
    /// </summary>
    Task ProcessToolCallsAsync(List<ToolCall> toolCalls, AppMode appMode, CancellationToken cancellationToken);

    /// <summary>
    /// Handles approval response from user.
    /// </summary>
    Task HandleApprovalAsync(string segmentId, bool approved);

    /// <summary>
    /// Handles user answer for ask_user tool.
    /// </summary>
    Task HandleAskUserAnswerAsync(string toolCallId, string answer);

    /// <summary>
    /// Cancels all pending approval and ask_user waiters.
    /// Called internally by PrepareToolsForApprovals before setting up new waiters,
    /// and externally for cleanup (e.g. SubAgentMessage.ReleaseMemory).
    /// </summary>
    void CancelPendingApprovals();
}
