using Microsoft.Extensions.AI;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Regression tests for the HTTP status travelling from the transport to the retry policy.
/// <para>
/// The status is the ONLY input of <see cref="LlmApiException.IsRetryable"/>, so anything that
/// rebuilds the exception on its way out of the gateway silently disables the whole
/// classification: a re-wrapped exception has <c>StatusCode = null</c>, which reads as
/// "cause unknown" and therefore as retryable. A 401 (wrong API key) then spent the full
/// budget — ten attempts, roughly nineteen minutes — before reporting a failure that was
/// knowable on the first attempt. That is exactly what a manual run showed.
/// </para>
/// <para>
/// Two wrappers sit between the transport and the caller and both used to re-wrap:
/// <see cref="AfCompletionsAdapter"/> (the non-streaming acquire) and
/// <see cref="AfCancelRace.GuardAsync{T}"/> (every streaming MoveNext). These tests pin the
/// status on BOTH paths, because only one of them was covered before and the defect survived.
/// </para>
/// </summary>
public class LlmApiStatusPropagationTests
{
    private const string UnauthorizedBody =
        """{"error":{"message":"Key not found","type":"authentication_error"}}""";

    /// <summary>
    /// Non-streaming path: the transport rejects the request before any body is read, the
    /// adapter must let the classified exception through instead of rebuilding it.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task GetCompletionsAsync_NonStreaming401_StatusSurvivesAdapter()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                .WithBody(UnauthorizedBody));

        var adapter = CreateAdapter(server.Urls[0], stream: false);

        var exception = await Assert.ThrowsAsync<LlmApiException>(() =>
            DrainAsync(adapter));

        Assert.Equal(401, exception.StatusCode);
        Assert.False(exception.IsRetryable);
    }

    /// <summary>
    /// Streaming path: the status is thrown on the FIRST MoveNext of the async iterator, so it
    /// travels through AfCancelRace.GuardAsync — the second wrapper that used to rebuild it.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task GetCompletionsAsync_Streaming401_StatusSurvivesCancelRaceGuard()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                .WithBody(UnauthorizedBody));

        var adapter = CreateAdapter(server.Urls[0], stream: true);

        var exception = await Assert.ThrowsAsync<LlmApiException>(() =>
            DrainAsync(adapter));

        Assert.Equal(401, exception.StatusCode);
        Assert.False(exception.IsRetryable);
    }

    /// <summary>
    /// The counterpart that must NOT change: a provider-side failure stays retryable, status
    /// and all. Without it a fix could have "solved" the 401 by dropping the status entirely.
    /// </summary>
    [Theory(Timeout = 20000)]
    [InlineData(500, false)]
    [InlineData(500, true)]
    [InlineData(429, false)]
    [InlineData(429, true)]
    public async Task GetCompletionsAsync_ProviderFailure_StaysRetryableWithStatus(int statusCode, bool stream)
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"upstream exploded"}}"""));

        var adapter = CreateAdapter(server.Urls[0], stream);

        var exception = await Assert.ThrowsAsync<LlmApiException>(() =>
            DrainAsync(adapter));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.True(exception.IsRetryable);
    }

    /// <summary>
    /// GuardAsync forwards an already-classified exception UNCHANGED — same instance, so no
    /// information (status, inner, stack) can be lost by the wrapper. Exercised directly on a
    /// hand-written stream, independent of the transport.
    /// </summary>
    [Fact]
    public async Task GuardAsync_AlreadyClassified_PassesInstanceThrough()
    {
        var original = LlmApiException.ForHttpStatus(403, "forbidden");

        var exception = await Assert.ThrowsAsync<LlmApiException>(async () =>
        {
            await foreach (var _ in AfCancelRace.GuardAsync(
                Faulting(original),
                () => "timeout",
                TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Same(original, exception);
    }

    /// <summary>
    /// A transport failure that has no status still gets wrapped by the guard (the original
    /// type/stack must not be hidden) and stays retryable.
    /// </summary>
    [Fact]
    public async Task GuardAsync_Unclassified_WrapsAndStaysRetryable()
    {
        var original = new HttpRequestException("connection reset");

        var exception = await Assert.ThrowsAsync<LlmApiException>(async () =>
        {
            await foreach (var _ in AfCancelRace.GuardAsync(
                Faulting(original),
                () => "timeout",
                TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Null(exception.StatusCode);
        Assert.True(exception.IsRetryable);
        Assert.Same(original, exception.InnerException);
    }

    private static AfCompletionsAdapter CreateAdapter(string endpoint, bool stream)
        => new(
            () => new ConnectionProfile
            {
                Endpoint = endpoint,
                ApiKey = "key",
                Model = "test-model",
                Stream = stream
            },
            new HttpClient(),
            () => 30);

    /// <summary>
    /// Enumerates the gateway and returns the first exception, so the test observes what a
    /// caller (the turn loop) actually receives instead of relying on a synchronous throw.
    /// </summary>
    private static async Task DrainAsync(AfCompletionsAdapter adapter)
    {
        await foreach (var _ in adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }],
            [],
            new ConversationSession { Id = "s" },
            new CompletionsResult(),
            TestContext.Current.CancellationToken))
        {
        }
    }

    /// <summary>
    /// One-element stream that throws instead of yielding: reproduces a failure raised INSIDE
    /// MoveNext, which is where both wrappers sit.
    /// </summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> Faulting(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // unreachable code: the yield is what makes this an iterator
        yield break;
#pragma warning restore CS0162
    }
}
