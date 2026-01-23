using Moq;
using UIBlazor.Models;
using UIBlazor.Options;
using UIBlazor.Services;

namespace UIBlazor.Tests;

public class ChatServiceTests
{
    private readonly Mock<IAiSettingsProvider> _aiSettingsProviderMock;
    private readonly Mock<IToolManager> _toolManagerMock;
    private readonly Mock<ILocalStorageService> _localStorageMock;

    public ChatServiceTests()
    {
        _aiSettingsProviderMock = new Mock<IAiSettingsProvider>();
        _toolManagerMock = new Mock<IToolManager>();
        _localStorageMock = new Mock<ILocalStorageService>();

        // Setup default options
        var options = new AiOptions
        {
            Endpoint = "https://api.test.com",
            ApiKey = "test-key",
            ApiKeyHeader = "Authorization",
            Model = "test-model",
            Temperature = 0.7f,
            MaxTokens = 1000,
            Stream = true,
            SystemPrompt = "Test system prompt",
            MaxRetryAttempts = 3,
            RetryDelaySeconds = 1,
            MaxMessages = 50
        };
        _aiSettingsProviderMock.SetupGet(p => p.Current).Returns(options);
        _toolManagerMock.Setup(tm => tm.GetToolUseSystemInstructions(It.IsAny<string>()))
            .Returns("Tool instructions");
    }

    private ChatService CreateChatService(HttpClient? httpClient = null)
    {
        return new ChatService(
            httpClient ?? new HttpClient(),
            _aiSettingsProviderMock.Object,
            _toolManagerMock.Object,
            _localStorageMock.Object);
    }

    [Fact]
    public async Task GetModelsAsync_MissingEndpoint_ThrowsException()
    {
        // Arrange
        _aiSettingsProviderMock.Setup(p => p.Current).Returns(new AiOptions { Endpoint = "" });
        var chatService = CreateChatService();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => chatService.GetModelsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ChatService_Options_ReturnsAiSettingsProviderCurrent()
    {
        // Arrange
        var chatService = CreateChatService();

        // Act
        var options = chatService.Options;

        // Assert
        Assert.Equal(_aiSettingsProviderMock.Object.Current, options);
    }

    [Fact]
    public async Task AddMessageAsync_AddsMessageToSession()
    {
        // Arrange
        var sessionId = "test-session";
        var session = new ConversationSession { Id = sessionId };
        _localStorageMock.Setup(ls => ls.GetItemAsync<ConversationSession>(sessionId))
            .ReturnsAsync(session);
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync())
            .ReturnsAsync(new List<string> { sessionId });

        var chatService = CreateChatService();

        // Act
        await chatService.AddMessageAsync(sessionId, "user", "Hello");

        // Assert
        _localStorageMock.Verify(ls => ls.SetItemAsync(sessionId, It.Is<ConversationSession>(s =>
            s.Messages.Count == 1 &&
            s.Messages[0].Role == "user" &&
            s.Messages[0].Content == "Hello")), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_ExistingSession_ReturnsExisting()
    {
        // Arrange
        var sessionId = "session_test";
        var existingSession = new ConversationSession { Id = sessionId };
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync())
            .ReturnsAsync([sessionId]);
        _localStorageMock.Setup(ls => ls.GetItemAsync<ConversationSession>(sessionId))
            .ReturnsAsync(existingSession);

        var chatService = CreateChatService();

        // Act
        var result = await chatService.GetOrCreateSessionAsync(sessionId);

        // Assert
        Assert.Equal(sessionId, result.Id);
        Assert.Equal(existingSession, result);
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_NewSession_ReturnsNew()
    {
        // Arrange
        var sessionId = "new-session";
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync())
            .ReturnsAsync(new List<string>());

        var chatService = CreateChatService();

        // Act
        var result = await chatService.GetOrCreateSessionAsync(sessionId);

        // Assert
        Assert.Equal(sessionId, result.Id);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public async Task ClearSessionAsync_RemovesSession()
    {
        // Arrange
        var sessionId = "test-session";
        var chatService = CreateChatService();

        // Act
        await chatService.ClearSessionAsync(sessionId);

        // Assert
        _localStorageMock.Verify(ls => ls.RemoveItemAsync(sessionId), Times.Once);
    }
}