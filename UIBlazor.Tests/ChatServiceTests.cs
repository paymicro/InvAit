using Moq;
using Shared.Contracts;
using UIBlazor.Models;
using UIBlazor.Options;
using UIBlazor.Services;
using UIBlazor.Services.Settings;
using UIBlazor.Tests.Helpers;
using System.Net;
using UIBlazor.Services.Models;
using System.Text.Json;
using System.Text;

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
        _toolManagerMock.Setup(tm => tm.GetToolUseSystemInstructions(It.IsAny<AppMode>(), false))
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

    [Fact]
    public async Task GetModelsAsync_Success_ReturnsModels()
    {
        // Arrange
        var expectedModels = new AiModelList ("object", [new AiModelItem ("model1", "object", 123456, "ownedBy")]);
        var handler = MockHttpMessageHandler.CreateJsonResponse(expectedModels);
        var httpClient = new HttpClient(handler);
        var chatService = CreateChatService(httpClient);

        // Act
        var result = await chatService.GetModelsAsync(CancellationToken.None);

        // Assert
        Assert.Single(result.Data);
        Assert.Equal("model1", result.Data[0].Id);
    }

    [Fact]
    public async Task GetModelsAsync_HttpError_ThrowsHttpRequestException()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Error message")
        }));
        var httpClient = new HttpClient(handler);
        var chatService = CreateChatService(httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => chatService.GetModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetModelsAsync_CustomHeader_SendsCorrectHeaders()
    {
        // Arrange
        HttpRequestMessage? receivedRequest = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            receivedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new AiModelList("object", [])), Encoding.UTF8, "application/json")
            });
        });
        
        var profile = new ConnectionProfile
        {
            Endpoint = "https://api.test.com",
            ApiKey = "test-key",
            ApiKeyHeader = "X-API-Key"
        };
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(profile);

        var httpClient = new HttpClient(handler);
        var chatService = CreateChatService(httpClient);

        // Act
        await chatService.GetModelsAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(receivedRequest);
        Assert.True(receivedRequest.Headers.Contains("X-API-Key"));
        Assert.Equal("test-key", receivedRequest.Headers.GetValues("X-API-Key").First());
    }

    [Fact]
    public async Task NewSessionAsync_SavesCurrent_AndCreatesNew()
    {
        // Arrange
        var chatService = CreateChatService();
        await chatService.AddMessageAsync("user", "Old message");
        var oldSessionId = chatService.Session.Id;
        await Task.Delay(1100, TestContext.Current.CancellationToken);

        // Act
        await chatService.NewSessionAsync();

        // Assert
        Assert.NotEqual(oldSessionId, chatService.Session.Id);
        Assert.Empty(chatService.Session.Messages);
        _localStorageMock.Verify(ls => ls.SetItemAsync(oldSessionId, It.IsAny<ConversationSession>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetRecentSessionsAsync_ReturnsOrderedSummaries()
    {
        // Arrange
        var now = DateTime.Now;
        var sessionId1 = "session_2024-01-01T12:00:00";
        var sessionId2 = "session_2024-01-01T13:00:00";
        var session1 = new ConversationSession { Id = sessionId1, CreatedAt = now.AddHours(-1), Messages = [new() { Role = "user", Content = "Msg1" }] };
        var session2 = new ConversationSession { Id = sessionId2, CreatedAt = now, Messages = [new() { Role = "user", Content = "Msg2" }] };

        _localStorageMock.Setup(ls => ls.GetAllKeysAsync()).ReturnsAsync([sessionId1, sessionId2]);
        _localStorageMock.Setup(ls => ls.GetItemAsync<ConversationSession>(sessionId1)).ReturnsAsync(session1);
        _localStorageMock.Setup(ls => ls.GetItemAsync<ConversationSession>(sessionId2)).ReturnsAsync(session2);

        var chatService = CreateChatService();

        // Act
        var result = await chatService.GetRecentSessionsAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(sessionId2, result[0].Id); // Latest first
        Assert.Equal(sessionId1, result[1].Id);
        Assert.Equal("Msg2", result[0].FirstUserMessage);
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesFromStorage()
    {
        // Arrange
        var chatService = CreateChatService();
        var sessionId = chatService.Session.Id;
        await Task.Delay(1100, TestContext.Current.CancellationToken);

        // Act
        await chatService.DeleteSessionAsync(sessionId);

        // Assert
        _localStorageMock.Verify(ls => ls.RemoveItemAsync(sessionId), Times.Once);
        Assert.NotEqual(sessionId, chatService.Session.Id); // Should create new session
    }

    [Fact]
    public async Task GetCompletionsAsync_NonStreaming_ReturnsMessage()
    {
        // Arrange
        var expectedChunk = new StreamChunk
        {
            Model = "test-model",
            Choices = [new ChatChoice { Message = new ChatDelta { Role = "assistant", Content = "Hello!" } }],
            Usage = new UsageInfo { TotalTokens = 10 }
        };
        var handler = MockHttpMessageHandler.CreateJsonResponse(expectedChunk);
        var httpClient = new HttpClient(handler);
        
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "https://api.test.com/",
            Stream = false
        });
        
        _toolManagerMock.Setup(tm => tm.GetToolUseSystemInstructions(It.IsAny<AppMode>(), It.IsAny<bool>())).Returns("instructions");
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync()).ReturnsAsync(new List<SkillMetadata>());
        _ruleServiceMock.Setup(r => r.GetRulesAsync()).ReturnsAsync("rules");

        var chatService = CreateChatService(httpClient);

        // Act
        var result = new List<ChatDelta>();
        await foreach (var delta in chatService.GetCompletionsAsync(CancellationToken.None))
        {
            result.Add(delta);
        }

        // Assert
        Assert.Single(result);
        Assert.Equal("Hello!", result[0].Content);
        Assert.Equal("test-model", chatService.LastCompletionsModel);
        Assert.Equal(10, chatService.LastUsage?.TotalTokens);
        Assert.Equal(10, chatService.Session.TotalTokens);
    }

    [Fact]
    public async Task GetCompletionsAsync_Streaming_ReturnsDeltas()
    {
        // Arrange
        var streamData = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\ndata: [DONE]\n";
        var handler = MockHttpMessageHandler.CreateStringResponse(streamData);
        var httpClient = new HttpClient(handler);
        
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "https://api.test.com/",
            Stream = true
        });
        
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync()).ReturnsAsync(new List<SkillMetadata>());

        var chatService = CreateChatService(httpClient);

        // Act
        var result = new List<ChatDelta>();
        await foreach (var delta in chatService.GetCompletionsAsync(CancellationToken.None))
        {
            result.Add(delta);
        }

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Hello", result[0].Content);
        Assert.Equal(" world", result[1].Content);
    }

    [Fact]
    public async Task GetCompletionsAsync_Streaming_HandlesThinkBlock()
    {
        // Arrange
        var streamData = "data: {\"choices\":[{\"delta\":{\"content\":\"<think>internal monologue\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"</think>Final answer\"}}]}\n\ndata: [DONE]\n";
        var handler = MockHttpMessageHandler.CreateStringResponse(streamData);
        var httpClient = new HttpClient(handler);
        
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "https://api.test.com/",
            Stream = true
        });
        
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync()).ReturnsAsync(new List<SkillMetadata>());

        var chatService = CreateChatService(httpClient);

        // Act
        var result = new List<ChatDelta>();
        await foreach (var delta in chatService.GetCompletionsAsync(CancellationToken.None))
        {
            result.Add(delta);
        }

        // Assert
        // Expected behavior in ChatService.cs:
        // Chunk 1: <think> detected -> reasoningContent = "internal monologue", content = null
        // Chunk 2: </think> detected -> content = "Final answer", reasoningContent = null
        Assert.Equal(2, result.Count);
        Assert.Null(result[0].Content);
        Assert.Equal("internal monologue", result[0].ReasoningContent);
        Assert.Equal("Final answer", result[1].Content);
        Assert.Null(result[1].ReasoningContent);
    }

    [Fact]
    public async Task GetCompletionsAsync_Streaming_BuffersSplitTags()
    {
        // Arrange
        // Tag <function> is split across two chunks: "<func" and "tion> name=\"test\">"
        var streamData = "data: {\"choices\":[{\"delta\":{\"content\":\"Text before <func\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"tion name=\\\"test\\\">\"}}]}\n\ndata: [DONE]\n";
        var handler = MockHttpMessageHandler.CreateStringResponse(streamData);
        var httpClient = new HttpClient(handler);
        
        _profileManagerMock.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "https://api.test.com/",
            Stream = true
        });
        
        _skillServiceMock.Setup(s => s.GetSkillsMetadataAsync()).ReturnsAsync(new List<SkillMetadata>());

        var chatService = CreateChatService(httpClient);

        // Act
        var result = new List<ChatDelta>();
        await foreach (var delta in chatService.GetCompletionsAsync(CancellationToken.None))
        {
            result.Add(delta);
        }

        // Assert
        // Chunk 1: "Text before " (buffers "<func")
        // Chunk 2: "<function name=\"test\">"
        Assert.Equal(2, result.Count);
        Assert.Equal("Text before ", result[0].Content);
        Assert.Equal("<function name=\"test\">", result[1].Content);
    }
}
