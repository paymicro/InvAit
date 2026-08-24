using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Coverage tests for ChatService internals refactoring: unified auth headers (ApplyApiKey),
/// session store operations (previews, cleanup, loading) and reasoning-token streaming estimates.
/// </summary>
public partial class ChatServiceTests
{
    private ChatService CreateChatServiceAt(WireMockServer server, string apiKey = "test-key", string apiKeyHeader = "Authorization")
    {
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = server.Urls[0],
            ApiKey = apiKey,
            ApiKeyHeader = apiKeyHeader,
            Model = "test-model",
            Temperature = 0.7,
            MaxTokens = 1000,
            Stream = true
        });
        return CreateChatService(server.CreateClient());
    }

    private static void SetupSseResponse(WireMockServer server, string sseBody)
    {
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "text/event-stream")
                    .WithHeader("Cache-Control", "no-cache")
                    .WithBody(sseBody)
            );
    }

    private static string RequestHeader(WireMockServer server, string name)
        => server.LogEntries
            .Select(e => e.RequestMessage.Headers)
            .Where(h => h != null)
            .SelectMany(h => h!.Where(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value))
            .SelectMany(v => v)
            .FirstOrDefault() ?? string.Empty;

    // ───────────────────────────────────────────────────────────────────────
    //  Auth headers (ApplyApiKey — unified between GetModelsAsync and GetCompletionsAsync)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCompletionsAsync_ApiKeyWithAuthorizationHeader_SendsBearerToken()
    {
        var server = WireMockServer.Start();
        SetupSseResponse(server, "data: [DONE]");
        var chatService = CreateChatServiceAt(server);

        await foreach (var _ in chatService.GetCompletionsAsync(new CompletionsResult(), TestContext.Current.CancellationToken)) { }

        Assert.Equal("Bearer test-key", RequestHeader(server, "Authorization"));
    }

    [Fact]
    public async Task GetCompletionsAsync_CustomApiKeyHeader_SendsHeaderVerbatim()
    {
        var server = WireMockServer.Start();
        SetupSseResponse(server, "data: [DONE]");
        var chatService = CreateChatServiceAt(server, apiKeyHeader: "X-Api-Key");

        await foreach (var _ in chatService.GetCompletionsAsync(new CompletionsResult(), TestContext.Current.CancellationToken)) { }

        Assert.Equal("test-key", RequestHeader(server, "X-Api-Key"));
        Assert.Empty(RequestHeader(server, "Authorization"));
    }

    [Fact]
    public async Task GetCompletionsAsync_NoApiKey_SendsNoAuthHeaders()
    {
        var server = WireMockServer.Start();
        SetupSseResponse(server, "data: [DONE]");
        var chatService = CreateChatServiceAt(server, apiKey: "");

        await foreach (var _ in chatService.GetCompletionsAsync(new CompletionsResult(), TestContext.Current.CancellationToken)) { }

        Assert.Empty(RequestHeader(server, "Authorization"));
        Assert.Empty(RequestHeader(server, "X-Api-Key"));
    }

    [Fact]
    public async Task GetModelsAsync_MissingApiKeyHeader_ThrowsInvalidOperationException()
    {
        // Unified validation: applies to BOTH endpoints now (completions path missed it before)
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "http://localhost",
            ApiKey = "key-without-header",
            ApiKeyHeader = "" // default is "Authorization" — must be cleared explicitly
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateChatService().GetModelsAsync(TestContext.Current.CancellationToken));
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Session store: previews and cleanup (GetRecentSessionsAsync)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRecentSessionsAsync_PreviewOver40Chars_IsTruncatedWithEllipsis()
    {
        const string id = "session_2025-01-01T00:00:00";
        var longMessage = new string('x', 60);
        var session = new ConversationSession { Id = id };
        session.SetMessages([new VisualChatMessage { Content = longMessage }]);
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync()).ReturnsAsync([id]);
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ConversationSession>(id)).ReturnsAsync(session);

        var summaries = await CreateChatService().GetRecentSessionsAsync(5);

        var summary = Assert.Single(summaries);
        Assert.Equal(longMessage[..40] + "...", summary.FirstUserMessage);
    }

    [Fact]
    public async Task GetRecentSessionsAsync_SessionWithoutUserMessages_IsDeletedFromStorage()
    {
        const string id = "session_2025-01-01T00:00:00";
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync()).ReturnsAsync([id]);
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ConversationSession>(id))
            .ReturnsAsync(new ConversationSession { Id = id }); // no messages

        var summaries = await CreateChatService().GetRecentSessionsAsync(5);

        Assert.Empty(summaries);
        _localStorageMock.Verify(ls => ls.RemoveItemAsync(id), Times.Once);
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Session loading
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadSessionAsync_UnknownId_KeepsCurrentSession()
    {
        var chatService = CreateChatService();
        var currentId = chatService.Session.Id;

        await chatService.LoadSessionAsync("session_does_not_exist");

        Assert.Equal(currentId, chatService.Session.Id);
    }

    [Fact]
    public async Task LoadLastSessionOrGenerateNewAsync_MultipleSessions_PicksNewestByEncodedDate()
    {
        const string olderId = "session_2024-01-01T00:00:00";
        const string newerId = "session_2025-06-15T10:00:00";
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync()).ReturnsAsync([olderId, newerId]);
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ConversationSession>(olderId))
            .ReturnsAsync(new ConversationSession { Id = olderId });
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ConversationSession>(newerId))
            .ReturnsAsync(new ConversationSession { Id = newerId });

        var chatService = CreateChatService();
        await chatService.LoadLastSessionOrGenerateNewAsync();

        Assert.Equal(newerId, chatService.Session.Id);
    }

    // ───────────────────────────────────────────────────────────────────────
    //  Streaming reasoning estimates: reasoning never occupies context
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCompletionsAsync_ReasoningDeltasWithoutDetails_EstimateExcludedFromContext()
    {
        // Heuristics are deterministic: "12345678" (8 chars) → 2 reasoning tokens,
        // "abcd" (4 chars) → 1 visible token. Usage has no completion_tokens_details,
        // so the accumulated reasoning estimate must be preserved and excluded:
        // visible = 100 - 2 = 98, session total = 200 - 2 = 198.
        var server = WireMockServer.Start();
        SetupSseResponse(server, """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"message":null,"delta":{"role":"assistant","content":null,"reasoning_content":"12345678","tool_calls":null},"finish_reason":null}]}
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"message":null,"delta":{"role":null,"content":"abcd","reasoning_content":null,"tool_calls":null},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":100,"total_tokens":200}}
            data: [DONE]
            """);
        var chatService = CreateChatServiceAt(server);

        // Mid-stream: while only reasoning has arrived, CompletionTokens must already grow
        // (it always tracks the FULL generation) — otherwise badges look frozen while thinking.
        var completionAfterReasoningOnly = -1;
        var reasoningAfterReasoningOnly = -1;

        var result = new CompletionsResult();
        await foreach (var delta in chatService.GetCompletionsAsync(result, TestContext.Current.CancellationToken))
        {
            if (delta.ReasoningContent is not null && completionAfterReasoningOnly < 0)
            {
                completionAfterReasoningOnly = result.CompletionTokens;
                reasoningAfterReasoningOnly = result.ReasoningTokens;
            }
        }

        // Heuristics are deterministic: "12345678" (8 chars) → 2 tokens.
        Assert.Equal(2, completionAfterReasoningOnly);      // full count includes reasoning mid-stream
        Assert.Equal(2, reasoningAfterReasoningOnly);

        // Final usage has no completion_tokens_details → heuristic split is preserved:
        // visible = 100 - 2 = 98, session total = 200 - 2 = 198. "abcd" adds 1 to the full count.
        Assert.Equal(2, result.ReasoningTokens);
        Assert.Equal(100, result.CompletionTokens);
        Assert.Equal(98, result.VisibleCompletionTokens);
        Assert.Equal(198, chatService.Session.TotalTokens);
    }

    [Fact]
    public async Task GetCompletionsAsync_ReasoningDetailsPresent_OverrideStreamingEstimate()
    {
        // Provider reports exact reasoning_tokens — they win over the char-based heuristic.
        var server = WireMockServer.Start();
        SetupSseResponse(server, """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"message":null,"delta":{"role":"assistant","content":null,"reasoning_content":"12345678","tool_calls":null},"finish_reason":null}]}
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":50,"completion_tokens":80,"total_tokens":130,"completion_tokens_details":{"reasoning_tokens":77}}}
            data: [DONE]
            """);
        var chatService = CreateChatServiceAt(server);

        var result = new CompletionsResult();
        await foreach (var _ in chatService.GetCompletionsAsync(result, TestContext.Current.CancellationToken)) { }

        Assert.Equal(77, result.ReasoningTokens);
        Assert.Equal(80, result.CompletionTokens);
        Assert.Equal(53, chatService.Session.TotalTokens);
    }
}
