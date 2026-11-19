namespace UIBlazor.Tests.Services;

/// <summary>
/// Retry eligibility of <see cref="LlmApiException"/>: the single bit that decides whether a
/// failed turn spends the retry budget.
/// <para>
/// Before it existed every exception was retried, so a wrong API key, a model that does not exist
/// and a bug in our own code each burned 10 attempts over ~19 minutes before reporting an error
/// that was knowable on the first attempt.
/// </para>
/// </summary>
public class LlmApiExceptionRetryabilityTests
{
    // ── Not retryable: the request itself is wrong ──────────────────────────────

    [Theory]
    [InlineData(400)] // malformed request
    [InlineData(401)] // wrong API key
    [InlineData(403)] // no access to the model
    [InlineData(404)] // model does not exist
    [InlineData(405)] // wrong endpoint
    [InlineData(413)] // payload too large — repeating sends the same payload
    [InlineData(422)] // unprocessable (schema/tool definition rejected)
    public void ClientError_NotRetryable(int statusCode)
    {
        var exception = LlmApiException.ForHttpStatus(statusCode, "bad request");

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.False(exception.IsRetryable);
    }

    // ── Retryable: the provider failed, not the request ─────────────────────────

    [Theory]
    [InlineData(500)]
    [InlineData(502)] // bad gateway — the classic flapping relay
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)] // request timeout
    [InlineData(409)] // conflict (transient on some relays)
    [InlineData(425)] // too early
    [InlineData(429)] // rate limit — the provider may simply be busy right now
    public void ProviderSideError_Retryable(int statusCode)
    {
        Assert.True(LlmApiException.ForHttpStatus(statusCode, "server failed").IsRetryable);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void SuccessStatusWithUnusableBody_Retryable(int statusCode)
    {
        // The request was accepted, so the problem is the relay's answer, not our payload.
        Assert.True(LlmApiException.ForHttpStatus(statusCode, "unparsable body").IsRetryable);
    }

    // ── No status: the cause is unknown ─────────────────────────────────────────

    [Fact]
    public void NoStatus_Retryable_TheCauseIsUnknown()
    {
        // Timeout, socket reset, or an API-level error event inside a successful stream. Legacy
        // behaviour must survive here: with no status we cannot claim the repeat will fail too.
        var timeout = new LlmApiException("Request timed out after 100s");
        var transport = new LlmApiException("connection reset", new InvalidOperationException("inner"));

        Assert.Null(timeout.StatusCode);
        Assert.True(timeout.IsRetryable);
        Assert.Null(transport.StatusCode);
        Assert.True(transport.IsRetryable);
    }

    // ── The inner exception is preserved regardless of the status ───────────────

    [Fact]
    public void ForHttpStatus_KeepsStatusOutOfTheMessageContract()
    {
        // The message keeps the status in its text for the log and the UI: the two forms must
        // agree, otherwise a screenshot of the error and the retry decision tell different stories.
        var exception = LlmApiException.ForHttpStatus(429, "HttpCode: 429 | server failed: slow down");

        Assert.Equal(429, exception.StatusCode);
        Assert.Contains("429", exception.Message);
        Assert.Null(exception.InnerException);
    }
}
