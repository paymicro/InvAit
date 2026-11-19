using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace UIBlazor.Tests.Services;

/// <summary>
/// A retry that ENDS IN SUCCESS, exercised through a real HTTP server.
/// <para>
/// The retry loop is covered elsewhere with a scripted in-process gateway, which proves the
/// loop's own arithmetic but says nothing about the transport. Three properties are only
/// observable when the second attempt really crosses the wire:
/// </para>
/// <list type="bullet">
/// <item>the gateway is still usable after a failed attempt — a disposed client or a poisoned
/// connection would turn the retry into a second failure;</item>
/// <item>the per-turn state in <see cref="CompletionsResult"/> does not leak between attempts —
/// the caller reuses ONE capture across the retries of a turn;</item>
/// <item>the failure is classified as retryable at the transport boundary, which is what makes
/// the loop retry at all.</item>
/// </list>
/// <para>
/// WireMock serves these in sequence through a scenario, so the same request is answered
/// differently per attempt — exactly the "flapping relay" the budget exists for.
/// </para>
/// </summary>
public class AfRetryThroughTransportTests
{
    private const string SuccessSse = """
        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}],"usage":null}

        data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":2,"total_tokens":13}}

        data: [DONE]

        """;

    /// <summary>
    /// Two provider failures, then a good answer: the third attempt delivers the content, and the
    /// caller sees a clean capture rather than one carrying the failures.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task GetCompletionsAsync_FailTwoThenSucceed_ThirdAttemptDeliversCleanResult()
    {
        using var server = WireMockServer.Start();

        // Attempt 1 and 2: the provider is down. Attempt 3: a normal SSE answer.
        // A scenario chains states, so the SAME request is answered differently per attempt.
        // The FIRST mapping must carry no WhenStateIs: the initial scenario state is empty, so
        // WhenStateIs("Started") never matches and the request would get a 404 - which is not
        // retryable, and the chain would fail for the wrong reason.
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .InScenario("flapping")
            .WillSetStateTo("StillDown")
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"upstream exploded"}}"""));

        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .InScenario("flapping")
            .WhenStateIs("StillDown")
            .WillSetStateTo("Recovered")
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"upstream still down"}}"""));

        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .InScenario("flapping")
            .WhenStateIs("Recovered")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithBody(SuccessSse));

        var adapter = new AfCompletionsAdapter(
            () => new ConnectionProfile { Endpoint = server.Urls[0], ApiKey = "key", Model = "test-model", Stream = true },
            new HttpClient(),
            () => 30);

        var session = new ConversationSession { Id = "s" };

        // ONE capture for all attempts: this is what the turn loop does, so a leak between
        // attempts would show up here.
        var capture = new CompletionsResult();

        var observations = new List<string>();
        string? answer = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var content = new StringBuilder();
                await foreach (var delta in adapter.GetCompletionsAsync(
                    [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }],
                    [], session, capture, TestContext.Current.CancellationToken))
                {
                    if (delta.Content is not null)
                        content.Append(delta.Content);
                }

                answer = content.ToString();
                break;
            }
            catch (LlmApiException ex)
            {
                observations.Add($"attempt {attempt}: status {ex.StatusCode}, retryable {ex.IsRetryable}");

                // The production decision: only a retryable failure is repeated. Anything else
                // (a 404 from a broken scenario, a rejected request) ends the loop here.
                if (!ex.IsRetryable)
                    break;
            }
        }

        // Two failures were observed, then the third attempt produced the answer.
        Assert.Equal(2, observations.Count);
        Assert.Equal("attempt 1: status 500, retryable True|attempt 2: status 500, retryable True",
            string.Join('|', observations));
        Assert.Equal("Hello", answer);

        // The capture belongs to the successful attempt only: no error text and no failure
        // residue from the attempts that never delivered a payload.
        Assert.Null(capture.Error);
        Assert.Equal("test-model", capture.Model);
        Assert.Equal("stop", capture.FinishReason);
        Assert.Equal(13, capture.Usage?.TotalTokens ?? capture.CompletionTokens);

        // The scenario really advanced: a fourth request would still succeed.
        Assert.Equal(3, server.LogEntries.Count());
    }

    /// <summary>
    /// The counterpart that the budget must NOT rescue: a rejected request is answered identically
    /// however many times it is repeated, so the transport flags it non-retryable and the loop
    /// gives up after one attempt.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task GetCompletionsAsync_AuthFailure_IsNotRetriedByTheCaller()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"message":"Key not found","type":"authentication_error"}}"""));

        var adapter = new AfCompletionsAdapter(
            () => new ConnectionProfile { Endpoint = server.Urls[0], ApiKey = "key", Model = "test-model", Stream = true },
            new HttpClient(),
            () => 30);

        var capture = new CompletionsResult();
        var failures = 0;

        // The production decision, verbatim: retry only when the transport says the repeat can
        // succeed.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await foreach (var _ in adapter.GetCompletionsAsync(
                    [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }],
                    [], new ConversationSession { Id = "s" }, capture, TestContext.Current.CancellationToken))
                {
                }

                Assert.Fail("the request must fail");
            }
            catch (LlmApiException ex)
            {
                failures++;
                if (!ex.IsRetryable)
                    break;
            }
        }

        // Exactly one attempt: the loop stopped because the failure is permanent.
        Assert.Equal(1, failures);
    }
}
