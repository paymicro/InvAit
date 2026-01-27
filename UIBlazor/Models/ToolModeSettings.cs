using Shared.Contracts;

namespace UIBlazor.Models;

public class ToolModeSettings
{
    public bool IsEnabled { get; set; } = true;
    
    public ToolApprovalMode ApprovalMode { get; set; } = ToolApprovalMode.AutoApprove;
}