namespace UIBlazor.Services.Models;

using System.Text.Json.Serialization;

public class ChatDelta
{
    /// <summary>
    /// The role of the message, which can be "system", "assistant", "user" or "tool"
    /// </summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>
    /// The content of the message with think block
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    private string? _reasoning;

    /// <summary>
    /// Optional. Content of message showed in think block
    /// </summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get => _reasoning; set => _reasoning = value; }

    /// <summary>
    /// Некоторые модели сюда пишут размышления
    /// </summary>
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get => _reasoning; set => _reasoning = value; }

    /// <summary>
    /// Optional. Native tools to be executed
    /// </summary>
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCall>? ToolCalls { get; set; }
}
