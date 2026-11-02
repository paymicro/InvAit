namespace UIBlazor.Models;

/// <summary>
/// Status of a sub-agent execution.
/// </summary>
public enum SubAgentStatus
{
    Pending,
    Running,
    /// <summary>
    /// The sub-agent is blocked waiting for user interaction: a tool call approval
    /// or an ask_user answer.
    /// </summary>
    NeedAction,
    Completed,
    Cancelled,
    Failed
}
