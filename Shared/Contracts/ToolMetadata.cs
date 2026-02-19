namespace Shared.Contracts;

public enum ToolCategory
{
    ReadFiles,
    WriteFiles,
    DeleteFiles,
    Browser,
    Mcp,
    ModeSwitch,
    Execution
}

public enum ToolApprovalMode
{
    AutoApprove,
    AlwaysAsk
}

public enum ToolApprovalStatus
{
    Pending,
    Approved,
    Rejected
}
