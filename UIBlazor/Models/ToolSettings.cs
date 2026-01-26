using Shared.Contracts;
using UIBlazor.Options;

namespace UIBlazor.Models;

public class ToolSettings : BaseOptions
{
    public Dictionary<string, ToolModeSettings> ToolStates { get; set; } = new();
}

public class ToolModeSettings
{
    public bool IsEnabled { get; set; } = true;
    public ToolApprovalMode ApprovalMode { get; set; } = ToolApprovalMode.Always;
}