using Shared.Contracts;
using UIBlazor.Options;

namespace UIBlazor.Models;

public class ToolSettings : BaseOptions
{
    public Dictionary<ToolCategory, ToolModeSettings> CategoryStates { get; set; } = [];
    public Dictionary<string, bool> ToolStates { get; set; } = [];
}
