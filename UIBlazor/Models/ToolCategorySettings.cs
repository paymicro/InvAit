namespace UIBlazor.Models;

public class ToolCategorySettings
{
    public bool IsEnabled { get; set; } = true;

    public ToolApprovalMode ApprovalMode { get; set; } = ToolApprovalMode.Allow;
}