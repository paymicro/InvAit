namespace UIBlazor.Services.Models;

/// <summary>
/// Thrown when an LLM API call fails at the transport level (non-success HTTP status code)
/// or the response stream reports an API-level error event.
/// Both cases are considered transient and eligible for retry by <c>SubAgentExecutor</c>.
/// </summary>
public sealed class LlmApiException(string message) : Exception(message);
