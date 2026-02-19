using System.Text.Json.Serialization;

namespace UIBlazor.Services.Models;

/// <summary>
/// Details of the function to be executed
/// </summary>
public class ToolCall
{
    [JsonPropertyName("index")]
    public int? Index { get; set; } = 0;

    /// <summary>
    /// Used later to submit the function result back to the AI.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// It will probably always be "function", indicating that the execution of a function is being requested.
    /// </summary>
    /// <returns>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    /// <summary>
    /// Gets or sets the function to call, represented by the <see cref="ToolCallFunction"/> object.
    /// </summary>
    [JsonPropertyName("function")]
    public ToolCallFunction Function { get; set; }
}
