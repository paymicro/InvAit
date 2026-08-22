using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace UIBlazor.Tests.Services;

/// <summary>
/// <seealso cref="ChatService"/>
/// </summary>
public partial class ChatServiceTests
{
    private readonly Mock<IProfileManager> _profileManagerMock;
    private readonly Mock<IToolManager> _toolManagerMock;
    private readonly Mock<ILocalStorageService> _localStorageMock;
    private readonly Mock<ISkillService> _skillServiceMock;

    public ChatServiceTests()
    {
        _profileManagerMock = new Mock<IProfileManager>();
        _toolManagerMock = new Mock<IToolManager>();
        _localStorageMock = new Mock<ILocalStorageService>();
        _skillServiceMock = new Mock<ISkillService>();

        // Setup default options
        var options = new ProfileOptions
        {
            Profiles = [],
            ActiveProfileId = "test"
        };
        _profileManagerMock.SetupGet(p => p.Current).Returns(options);
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "",
            ApiKey = "test-key",
            ApiKeyHeader = "Authorization",
            Model = "test-model",
            Temperature = 0.7,
            MaxTokens = 1000,
            Stream = true,
            SystemPrompt = "Test system prompt",
            TokensToCompress = 50000
        });

        // Default setup for session listing
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync()).ReturnsAsync([]);
    }

    private ChatService CreateChatService(HttpClient? httpClient = null)
    {
        return new ChatService(
            httpClient ?? new HttpClient(),
            _profileManagerMock.Object,
            Mock.Of<ISystemPromptBuilder>(),
            _localStorageMock.Object,
            new LoggerMock<IChatService>(),
            _toolManagerMock.Object);
    }

    [Fact]
    public async Task GetModelsAsync_MissingEndpoint_ThrowsException()
    {
        // Arrange
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile { Endpoint = "" });
        var chatService = CreateChatService();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => chatService.GetModelsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ChatService_Options_ReturnsProfileManagerActiveProfile()
    {
        // Arrange
        var chatService = CreateChatService();

        // Act
        var options = chatService.Options;

        // Assert
        Assert.Equal(_profileManagerMock.Object.ActiveProfile, options);
    }

    [Fact]
    public async Task LoadLastSessionOrGenerateNewAsync_ExistingSession_LoadsIt()
    {
        // Arrange
        var sessionId = "session_2024-01-01T12:00:00";
        var existingSession = new ConversationSession { Id = sessionId, Messages = [new() { Content = "Hi" }] };
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync())
            .ReturnsAsync([sessionId]);
        _localStorageMock.Setup(ls => ls.TryGetItemAsync<ConversationSession>(sessionId))
            .ReturnsAsync(existingSession);

        var chatService = CreateChatService();

        // Act
        await chatService.LoadLastSessionOrGenerateNewAsync();

        // Assert
        Assert.Equal(sessionId, chatService.Session.Id);
        Assert.Single(chatService.Session.Messages);
    }

    [Fact]
    public async Task LoadLastSessionOrGenerateNewAsync_NoSession_CreatesNew()
    {
        // Arrange
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync())
            .ReturnsAsync(new List<string>());

        var chatService = CreateChatService();

        // Act
        await chatService.LoadLastSessionOrGenerateNewAsync();

        // Assert
        Assert.NotNull(chatService.Session);
        Assert.StartsWith("session_", chatService.Session.Id);
        Assert.Empty(chatService.Session.Messages);
    }

    [Fact]
    public async Task GetCompletionsAsync_StreamsSseDeltas_BasicContent()
    {
        // Arrange - SSE format with delta array
        var sseResponse = """
            data: {"id":"d9d528e22108450d96716ce5f36fb2ea","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"role":"assistant","content":"Hello","reasoning_content":null,"tool_calls":null},"finish_reason":null}],"usage":null}
            data: {"id":"d9d528e22108450d96716ce5f36fb2ea","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"role":null,"content":" world","reasoning_content":null,"tool_calls":null},"finish_reason":null}],"usage":null}
            data: {"id":"d9d528e22108450d96716ce5f36fb2ea","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"role":null,"content":"!","reasoning_content":null,"tool_calls":null},"finish_reason":null}],"usage":null}
            data: [DONE]
            """;


        var server = WireMockServer.Start();
        var httpClient = server.CreateClient();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "text/event-stream")
                    .WithHeader("Cache-Control", "no-cache")
                    .WithBody(sseResponse)
            );

        var chatService = CreateChatService(httpClient);

        // Act
        var result = new CompletionsResult();
        var deltas = new List<ChatDelta>();
        await foreach (var delta in chatService.GetCompletionsAsync(result, TestContext.Current.CancellationToken))
        {
            deltas.Add(delta);
        }

        // Assert
        Assert.Equal(3, deltas.Count);
        Assert.Equal("assistant", deltas[0].Role);
        Assert.Equal("Hello", deltas[0].Content);
        Assert.Equal(" world", deltas[1].Content);
        Assert.Equal("!", deltas[2].Content);
    }

    [Fact]
    public async Task GetCompletionsAsync_Error_ReturnError()
    {
        // Arrange
        var sseResponse = "data: {\"error\":{\"message\":\"The number of tokens to keep from the initial prompt is greater than the context length (n_keep: 23678>= n_ctx: 9472). Try to load the model with a larger context length, or provide a shorter input.\"}}";

        var server = WireMockServer.Start();
        var httpClient = server.CreateClient();
        server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "text/event-stream")
                    .WithHeader("Cache-Control", "no-cache")
                    .WithBody(sseResponse)
            );
        var chatService = CreateChatService(httpClient);

        // Act
        var result = new CompletionsResult();
        var deltas = new List<ChatDelta>();
        await foreach (var delta in chatService.GetCompletionsAsync(result, TestContext.Current.CancellationToken))
        {
            deltas.Add(delta);
        }

        // Assert
        Assert.Empty(deltas);
        Assert.Equal(sseResponse[6..], result.Error);
    }

    private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }

    public static string EscapeJsonChar(char c)
    {
        return c switch
        {
            '\"' => "\\\"",
            '\\' => "\\\\",
            '/' => "\\/",
            '\b' => "\\b",
            '\f' => "\\f",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ when char.IsControl(c) => $"\\u{(int)c:x4}",
            _ => c.ToString()
        };
    }
}
