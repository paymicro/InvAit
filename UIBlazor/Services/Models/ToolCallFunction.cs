using System.Text.Json.Serialization;

namespace UIBlazor.Services.Models;

/// <summary>
/// Represents a class that encapsulates a function or method to be called.
/// </summary>
public class ToolCallFunction
{
    /// <summary>
    /// The name of the function to be executed.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the function's arguments.
    /// </summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}
