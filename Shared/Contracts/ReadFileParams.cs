using System.ComponentModel;

namespace Shared.Contracts;

public class ReadFileParams
{
    [Description("Path to file. Absolute or relative")]
    public string Name { get; set; } = string.Empty;
    [Description("Start line")]
    public int StartLine { get; set; } = -1;
    [Description("Line count")]
    public int LineCount { get; set; } = -1;
}
