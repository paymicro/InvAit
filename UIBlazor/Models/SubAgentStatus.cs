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
    /// <summary>
    /// The sub-agent is paused by the user: the agent loop waits at the next
    /// turn boundary until resumed.
    /// </summary>
    Paused,
    Completed,
    Cancelled,
    Failed
}
