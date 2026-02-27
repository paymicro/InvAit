using Moq;
using Shared.Contracts;
using UIBlazor.Models;
using UIBlazor.Options;
using UIBlazor.Services;
using UIBlazor.Services.Settings;

namespace UIBlazor.Tests;

public class ChatServiceTests
{
    private readonly Mock<IProfileManager> _profileManagerMock;
    private readonly Mock<IToolManager> _toolManagerMock;
    private readonly Mock<ILocalStorageService> _localStorageMock;
    private readonly Mock<ISkillService> _skillServiceMock;
    private readonly Mock<IRuleService> _ruleServiceMock;
    private readonly Mock<IVsCodeContextService> _vsCodeContextServiceMock;

    public ChatServiceTests()
    {
        _profileManagerMock = new Mock<IProfileManager>();
        _toolManagerMock = new Mock<IToolManager>();
        _localStorageMock = new Mock<ILocalStorageService>();
        _skillServiceMock = new Mock<ISkillService>();
        _ruleServiceMock = new Mock<IRuleService>();
        _vsCodeContextServiceMock = new Mock<IVsCodeContextService>();

        // Setup default options
        var options = new ProfileOptions
        {
            Profiles = [],
            ActiveProfileId = "test"
        };
        _profileManagerMock.SetupGet(p => p.Current).Returns(options);
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "https://api.test.com",
            ApiKey = "test-key",
            ApiKeyHeader = "Authorization",
            Model = "test-model",
            Temperature = 0.7,
            MaxTokens = 1000,
            Stream = true,
            SystemPrompt = "Test system prompt",
            MaxRetryAttempts = 3,
            RetryDelaySeconds = 1,
            MaxMessages = 50
        });
        _toolManagerMock.Setup(tm => tm.GetToolUseSystemInstructions(It.IsAny<AppMode>()))
            .Returns("Tool instructions");

        // Default setup for session listing
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync()).ReturnsAsync([]);
    }

    private ChatService CreateChatService(HttpClient? httpClient = null)
    {
        return new ChatService(
            httpClient ?? new HttpClient(),
            _profileManagerMock.Object,
            Mock.Of<ICommonSettingsProvider>(),
            _toolManagerMock.Object,
            _localStorageMock.Object,
            _skillServiceMock.Object,
            _ruleServiceMock.Object,
            _vsCodeContextServiceMock.Object);
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
    public async Task AddMessageAsync_AddsMessageToSession()
    {
        // Arrange
        var chatService = CreateChatService();
        await chatService.LoadLastSessionOrGenerateNewAsync();
        var sessionId = chatService.Session.Id;

        // Act
        await chatService.AddMessageAsync("user", "Hello");

        // Assert
        Assert.Single(chatService.Session.Messages);
        Assert.Equal("user", chatService.Session.Messages[0].Role);
        Assert.Equal("Hello", chatService.Session.Messages[0].Content);

        _localStorageMock.Verify(ls => ls.SetItemAsync(sessionId, It.Is<ConversationSession>(s =>
            s.Messages.Count == 1 &&
            s.Messages[0].Role == "user" &&
            s.Messages[0].Content == "Hello")), Times.Once);
    }

    [Fact]
    public async Task LoadLastSessionOrGenerateNewAsync_ExistingSession_LoadsIt()
    {
        // Arrange
        var sessionId = "session_2024-01-01T12:00:00";
        var existingSession = new ConversationSession { Id = sessionId, Messages = [new() { Content = "Hi" }] };
        _localStorageMock.Setup(ls => ls.GetAllKeysAsync())
            .ReturnsAsync([sessionId]);
        _localStorageMock.Setup(ls => ls.GetItemAsync<ConversationSession>(sessionId))
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
}
