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
    public ToolApprovalStatus? ApprovalStatus { get; set; } = ToolApprovalStatus.Approved;

    [JsonPropertyName("result")]
    public ToolResult? Result { get; set; }

    /// <summary>
    /// UTC moment when the tool body was actually invoked. Stamped by
    /// <see cref="ToolCallHandler"/> AFTER the approval was resolved and immediately
    /// before the tool is executed, so the approval wait (and the wait in the
    /// sequential batch queue) is never part of the measured interval.
    /// Never persisted: after a session reload an in-flight call must not show a
    /// "zombie" countdown for a tool that is not running in this process anymore.
    /// </summary>
    [JsonIgnore]
    public DateTime? ExecutionStartedAtUtc { get; set; }

    /// <summary>
    /// Measured execution duration in ms (start → result). Persisted with the session,
    /// so the finished-state badge survives reload and compression.
    /// </summary>
    [JsonPropertyName("executionElapsedMs")]
    public long? ExecutionElapsedMs { get; set; }

    /// <summary>True while the tool body runs: start stamped, duration not measured yet.</summary>
    [JsonIgnore]
    public bool IsExecuting => ExecutionStartedAtUtc is not null && ExecutionElapsedMs is null;

    /// <summary>
    /// Raised when the execution clock changes state (start stamped / duration measured).
    /// The handler stamps the clock outside the render cascade, and nothing else would make
    /// the badge re-render at that moment (a running tool produces no parent re-renders).
    /// The rendering <see cref="ToolTimeoutBadge"/> subscribes to it — same pattern as
    /// <see cref="SubAgentsChanged"/>.
    /// </summary>
    public event Action? ExecutionClockChanged;

    /// <summary>
    /// Stamps the execution start. Idempotent: a repeated call keeps the first stamp,
    /// so a re-entered execution path cannot shift the clock.
    /// </summary>
    public void MarkExecutionStarted()
    {
        if (ExecutionStartedAtUtc is not null)
            return;

        ExecutionStartedAtUtc = DateTime.UtcNow;
        NotifyExecutionClockChanged();
    }

    /// <summary>
    /// Measures the execution duration from <see cref="ExecutionStartedAtUtc"/>.
    /// No-op when the clock never started (denied / not executed / ask_user) and when
    /// the duration is already recorded (first measurement wins).
    /// </summary>
    public void MarkExecutionFinished()
    {
        if (ExecutionElapsedMs is not null || ExecutionStartedAtUtc is not { } startedAt)
            return;

        var elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
        ExecutionElapsedMs = (long)Math.Max(0, elapsedMs);
        NotifyExecutionClockChanged();
    }

    private void NotifyExecutionClockChanged()
        => Volatile.Read(ref ExecutionClockChanged)?.Invoke();

    /// <summary>
    /// All sub-agent branches created by this delegate_task call
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
