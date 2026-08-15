using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Shared.Contracts;

public class ReadFileParams
{
    [Description("Path to file. Absolute or relative. Unique in list")]
    [JsonPropertyName("path")]
    public string Path { get; set; } = null!;

    [Description("Start line. 1-based. Default: 1")]
    [DefaultValue(-1)]
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; } = -1;

    [Description("Line count. Maximum: 2000")]
    [DefaultValue(-1)]
    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; } = -1;
}
