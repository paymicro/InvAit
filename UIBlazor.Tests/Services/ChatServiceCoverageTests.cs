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
            Temperature = 0.7f,
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
        // The custom transport applies LlmAuthHeader (legacy parity): only the custom
        // key header is sent, without an additional Bearer Authorization.
        var server = WireMockServer.Start();
        SetupSseResponse(server, "data: [DONE]");
        var chatService = CreateChatServiceAt(server, apiKeyHeader: "X-Api-Key");

        await foreach (var _ in chatService.GetCompletionsAsync(new CompletionsResult(), TestContext.Current.CancellationToken)) { }

        Assert.Equal("test-key", RequestHeader(server, "X-Api-Key"));
    }

    [Fact]
    public async Task GetCompletionsAsync_NoApiKey_SendsNoAuthHeaders()
    {
        // The custom transport applies LlmAuthHeader (legacy parity): an empty ApiKey
        // means no auth headers at all — no placeholder, no Bearer.
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
    //  Streaming reasoning estimates moved to adapter level: AfAgentFrameworkTests
    //  (GetCompletionsAsync_UsageWithoutReasoningDetails / ReasoningDetailsPresent).
    // ───────────────────────────────────────────────────────────────────────
}
