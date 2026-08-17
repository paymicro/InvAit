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
    public ToolCallFunction Function { get; set; } = new();

    [JsonIgnore]
    public int Tokens => (int)(8 + (Result?.Content.Length ?? 0) / 3.5);

    [JsonIgnore]
    public bool IsReady { get; set; } = true;

    [JsonIgnore]
    public ToolApprovalStatus? ApprovalStatus { get; set; } = ToolApprovalStatus.Approved;

    [JsonPropertyName("result")]
    public ToolResult? Result { get; set; }

    /// <summary>
    /// Sub-agent data if this tool call is a delegate_task.
    /// Bound to the specific tool call, not the parent message,
    /// so multiple delegate_task calls in one message each have their own sub-agent.
    /// </summary>
    [JsonIgnore]
    public SubAgentMessage? SubAgent { get; set; }
}
