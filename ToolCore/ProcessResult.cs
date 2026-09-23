namespace ToolCore;

/// <summary>
/// Result of process execution
/// </summary>
public class ProcessResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    /// <summary>
    /// True when the process was terminated because the timeout was reached.
    /// </summary>
    public bool TimedOut { get; set; }
    /// <summary>
    /// True when the process was terminated because the caller requested cancellation.
    /// </summary>
    public bool Cancelled { get; set; }
    /// <summary>
    /// True when the process was killed (by timeout, cancellation, or parent termination).
    /// </summary>
    public bool WasKilled { get; set; }
}
