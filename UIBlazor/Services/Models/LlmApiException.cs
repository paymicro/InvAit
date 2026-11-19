namespace UIBlazor.Services.Models;

/// <summary>
/// Thrown when an LLM API call fails: a non-success HTTP status, a transport failure, a burned
/// timeout window, or an API-level error event inside the response stream.
/// <para>
/// The type carries the HTTP status when there is one, and derives from it whether re-issuing the
/// SAME request can plausibly succeed. That single bit is what keeps the retry budget from being
/// spent on failures that are certain to fail again: before it existed, every exception — wrong
/// API key, malformed request, a bug in our own code — burned the full budget (10 attempts,
/// ~19 minutes of waiting) before reporting an error that was knowable on the first attempt.
/// </para>
/// </summary>
public sealed class LlmApiException : Exception
{
    private LlmApiException(string message, int? statusCode, Exception? innerException)
        : base(message, innerException)
        => StatusCode = statusCode;

    /// <summary>
    /// A failure with no HTTP status: a transport error, a timeout, or an error event reported
    /// inside an otherwise successful response stream.
    /// </summary>
    public LlmApiException(string message) : this(message, statusCode: null, innerException: null) { }

    /// <summary>
    /// Preserves the original exception (type/stack/inner) — avoids hiding the origin of
    /// bugs (the message alone used to hide it).
    /// </summary>
    public LlmApiException(string message, Exception innerException)
        : this(message, statusCode: null, innerException: innerException) { }

    /// <summary>
    /// The HTTP status that failed, or <c>null</c> when the failure has no status (see
    /// <see cref="LlmApiException(string)"/>).
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// Whether re-issuing the SAME request can plausibly succeed.
    /// <para>
    /// Retryable: no status (the cause is unknown — timeout, socket reset, an error event from a
    /// relay), <c>5xx</c> (the provider failed, not the request), <c>408</c>/<c>409</c>/<c>425</c>/
    /// <c>429</c> (transient by definition), and <c>2xx</c> (the request was accepted but the body
    /// was unusable — a broken relay).
    /// </para>
    /// <para>
    /// Not retryable: every other <c>4xx</c>. A wrong API key, a model that does not exist and a
    /// malformed request produce exactly the same answer ten times; the user needs the reason, not
    /// nineteen minutes of silence.
    /// </para>
    /// </summary>
    public bool IsRetryable => StatusCode switch
    {
        // No status: timeout, transport, or a mid-stream API error. The cause is unknown, so the
        // retry is the only way to find out whether it was transient (legacy behaviour).
        null => true,

        // 2xx that failed anyway: the body could not be parsed — a flaky relay, not our request.
        >= 200 and < 300 => true,

        // Provider-side failures.
        >= 500 => true,

        // Transient by definition.
        408 or 409 or 425 or 429 => true,

        // Any other 4xx: the request itself is wrong, repeating it changes nothing.
        _ => false,
    };

    /// <summary>
    /// A failed HTTP request. The message keeps the status in its text for the log and the UI;
    /// <paramref name="statusCode"/> is the machine-readable form the retry policy reads.
    /// </summary>
    public static LlmApiException ForHttpStatus(int statusCode, string message)
        => new(message, statusCode, innerException: null);
}
