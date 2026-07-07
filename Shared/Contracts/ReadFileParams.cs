using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Shared.Contracts;

public class ReadFileParams
{
    [Description("Path to file. Absolute or relative")]
    [JsonPropertyName("path")]
    public string Path { get; set; } = null!;

    [Description("Start line")]
    [DefaultValue(-1)]
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; } = -1;

    [Description("Line count")]
    [DefaultValue(-1)]
    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; } = -1;
}
