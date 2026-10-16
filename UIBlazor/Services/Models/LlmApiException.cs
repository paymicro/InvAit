namespace UIBlazor.Services.Models;

/// <summary>
/// Thrown when an LLM API call fails at the transport level (non-success HTTP status code)
/// or the response stream reports an API-level error event.
/// Both cases are considered transient and eligible for retry by the agent loop retry logic.
/// </summary>
public sealed class LlmApiException : Exception
{
    public LlmApiException(string message) : base(message) { }

    /// <summary>
    /// Preserves the original exception (type/stack/inner) — avoids hiding the origin of
    /// bugs (the message alone used to hide it).
    /// </summary>
    public LlmApiException(string message, Exception innerException) : base(message, innerException) { }
}
