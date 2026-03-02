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
    AutoApprove,
    AlwaysAsk
}

public enum ToolApprovalStatus
{
    Pending,
    Approved,
    Rejected
}
