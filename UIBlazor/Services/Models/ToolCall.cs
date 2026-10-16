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
    /// Single-task shape (legacy / one branch): set together with the first element below.
    /// Phase 2c: one delegate_task call can carry several tasks executed in parallel
    /// (Workflows fan-out), each rendered as its own SubAgentView.
    /// </summary>
    [JsonIgnore]
    public SubAgentMessage? SubAgent { get; set; }

    /// <summary>
    /// All sub-agent branches created by this delegate_task call (Phase 2c).
    /// </summary>
    [JsonIgnore]
    public List<SubAgentMessage>? SubAgents { get; set; }

    /// <summary>
    /// Nesting depth of this delegate_task call: 0 — called by the main agent,
    /// 1+ — called by a sub-agent (used to enforce MaxSubAgentDepth recursion cap).
    /// </summary>
    [JsonIgnore]
    public int SubAgentDepth { get; set; }

    /// <summary>
    /// Raised when SubAgents/SubAgent attach to this call (orchestrator fan-out without
    /// existing rows or a nested batch later). The rendering ToolCallBlock subscribes
    /// in OnInitialized and re-renders itself on each raise: attach discovery must not
    /// rely on parent re-render cascades, because the Blazor renderer SKIPS re-processing
    /// child components whose parameters are unchanged.
    /// </summary>
    public event Action? SubAgentsChanged;

    /// <summary>
    /// Raises <see cref="SubAgentsChanged"/> after an attach-mutation of SubAgents/SubAgent.
    /// </summary>
    public void NotifySubAgentsChanged()
        => Volatile.Read(ref SubAgentsChanged)?.Invoke();
}
