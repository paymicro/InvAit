using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Shared.Contracts;

public class ReadFileParams
{
    [Description("Path to file. Absolute or relative. Unique in list")]
    [JsonPropertyName("path")]
    public string Path { get; set; } = null!;

    // НЕТ [DefaultValue]: он делал свойство "optional" для SchemaProcessor, тот заворачивал тип в
    // ["integer","null"], и модель получала право прислать `"startLine": null`. STJ на non-nullable
    // int бросает, JsonUtils.GetObject глотает исключение и отдаёт null, и хост падал
    // NullReferenceException'ом на `.Count`. Точно тот же класс, из-за которого DiffEdit.ReplaceAll
    // стал bool?. Сентинел описан в тексте, а не в типе.
    [Description("Start line. 1-based. -1 = read from the top of the file")]
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; } = -1;

    [Description("Line count. Maximum: 2000. -1 = read to the end of the file")]
    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; } = -1;
}
