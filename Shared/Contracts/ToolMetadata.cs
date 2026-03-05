namespace Shared.Contracts;

public enum ToolCategory
{
    ReadFiles,
    WriteFiles,
    DeleteFiles,
    Mcp,
    ModeSwitch,
    Execution
}

public enum ToolApprovalMode
{
    Allow,
    Ask,
    Deny
}

public enum ToolApprovalStatus
{
    Pending,
    Approved,
    Rejected
}
