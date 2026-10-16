namespace UIBlazor.Models;

/// <summary>
/// Status of a sub-agent execution.
/// </summary>
public enum SubAgentStatus
{
    Pending,
    Running,
    Completed,
    Cancelled,
    Failed
}
