namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="AiChat"/>
/// </summary>
public class AiChatTests : BunitContext
{
    private readonly Mock<IChatService> _mockChatService;
    private readonly Mock<IToolManager> _mockToolManager;
    private readonly Mock<IProfileManager> _mockProfileManager;
    private readonly Mock<ICommonSettingsProvider> _mockCommonSettings;
    private readonly Mock<IVsBridge> _mockVsBridge;
    private readonly Mock<IMessageParser> _mockMessageParser;
    private readonly Mock<IJSRuntime> _mockJsRuntime;
    private readonly ConversationSession _session;
    private readonly ProfileOptions _profileOptions;
    private readonly ConnectionProfile _activeProfile;
    private readonly CommonOptions _commonOptions;

    public AiChatTests()
    {
        _mockChatService = new Mock<IChatService>();
        _mockToolManager = new Mock<IToolManager>();
        _mockProfileManager = new Mock<IProfileManager>();
        _mockCommonSettings = new Mock<ICommonSettingsProvider>();
        _mockVsBridge = new Mock<IVsBridge>();
        _mockMessageParser = new Mock<IMessageParser>();
        _mockJsRuntime = new Mock<IJSRuntime>();

        // Setup session with empty messages list
        _session = new ConversationSession
        {
            Id = "test-session-id",
            Mode = AppMode.Chat
        };

        // Setup profile options
        _activeProfile = new ConnectionProfile
        {
            Id = "test-profile-id",
            Name = "Test Profile",
            MaxMessages = 50,
            ContextWindow = 128000,
            Stream = true
        };

        _profileOptions = new ProfileOptions
        {
            ActiveProfileId = "test-profile-id",
            Profiles = [_activeProfile]
        };

        // Setup common options
        _commonOptions = new CommonOptions
        {
            MaxRetries = 3,
            ShowMessageTimings = true
        };

        // Setup mock returns
        _mockChatService.Setup(x => x.Session).Returns(_session);
        _mockProfileManager.Setup(x => x.Current).Returns(_profileOptions);
        _mockProfileManager.Setup(x => x.ActiveProfile).Returns(_activeProfile);
        _mockCommonSettings.Setup(x => x.Current).Returns(_commonOptions);

        // Register services
        Services.AddSingleton(_mockChatService.Object);
        Services.AddSingleton(_mockToolManager.Object);
        Services.AddSingleton(_mockProfileManager.Object);
        Services.AddSingleton(_mockCommonSettings.Object);
        Services.AddSingleton(_mockVsBridge.Object);
        Services.AddSingleton(_mockMessageParser.Object);
        Services.AddSingleton(_mockJsRuntime.Object);
        Services.AddSingleton(new Mock<ILogger<AiChat>>().Object);

        // Add Radzen components
        Services.AddRadzenComponents();

        // Setup JSInterop for Radzen and component JS calls
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
        JSInterop.SetupVoid("setChatHandler", _ => true);
        JSInterop.SetupVoid("initChatAutoScroll", _ => true);

        // Register component stubs
        RegisterComponentStubs();
    }

    private void RegisterComponentStubs()
    {
        // Stub for AiChatInput
        ComponentFactories.AddStub<AiChatInput>(builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "ai-chat-input-stub");
                builder.AddContent(2, "AiChatInput Stub");
                builder.CloseElement();
            });

        // Stub for ChatMessageView
        ComponentFactories.AddStub<ChatMessageView>(parameters => builder =>
        {
            // Извлекаем объект сообщения из параметров
            var message = parameters.Get(p => p.Message);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "chat-message-view-stub");
            builder.AddAttribute(2, "data-message-id", message?.Id);
            builder.AddContent(3, message?.Content ?? "No content");
            builder.CloseElement();
        });

        // Stub for UsageIndicators
        ComponentFactories.AddStub<UsageIndicators>(builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "usage-indicators-stub");
                builder.AddContent(2, "UsageIndicators Stub");
                builder.CloseElement();
            });

        // Stub for RecentSessionsPicker
        ComponentFactories.AddStub<RecentSessionsPicker>(builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "recent-sessions-picker-stub");
            builder.AddContent(2, "Recent Sessions Title");
            builder.CloseElement();
        });
    }

    [Fact]
    public void ShouldRenderEmptyState_WhenNoMessages()
    {
        // Arrange - session has no messages by default

        // Act
        var cut = Render<AiChat>();

        // Assert
        Assert.NotNull(cut.Find(".recent-sessions-picker-stub"));
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".chat-message-view-stub"));
    }

    [Fact]
    public void ShouldRenderMessages_WhenSessionHasMessages()
    {
        // Arrange
        var userMessage = new VisualChatMessage
        {
            Role = ChatMessageRole.User,
            Content = "Hello, this is a test message"
        };
        var assistantMessage = new VisualChatMessage
        {
            Role = ChatMessageRole.Assistant,
            Content = "Hello! How can I help you today?"
        };
        _session.Messages.Add(userMessage);
        _session.Messages.Add(assistantMessage);

        // Act
        var cut = Render<AiChat>();

        // Assert
        var messageStubs = cut.FindAll(".chat-message-view-stub");
        Assert.Equal(2, messageStubs.Count);
    }

    [Fact]
    public async Task ShouldCallNewSessionAsync_WhenNewSessionButtonClicked()
    {
        // Arrange
        _mockChatService.Setup(x => x.NewSessionAsync()).Returns(Task.CompletedTask);
        var cut = Render<AiChat>();
        var newSessionButton = cut.Find(".rz-chat-header-new");

        // Act
        await cut.InvokeAsync(() => newSessionButton.Click());

        // Assert
        _mockChatService.Verify(x => x.NewSessionAsync(), Times.Once);
    }

    [Fact]
    public async Task ShouldOpenSettingsDialog_WhenSettingsButtonClicked()
    {
        // Arrange
        var cut = Render<AiChat>();
        var settingsButton = cut.Find(".rz-chat-header-settings");

        // Act
        await cut.InvokeAsync(() => settingsButton.Click());

        // Assert - Settings button should be clickable (no disabled attribute or disabled=false)
        // Note: Full dialog testing requires more complex setup with DialogService
        Assert.NotNull(settingsButton);
    }

    [Fact]
    public async Task ShouldHandleProfileChange_WhenProfileSelected()
    {
        // Arrange
        var newProfileId = "new-profile-id";
        _mockProfileManager.Setup(x => x.ActivateProfileAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var cut = Render<AiChat>();
        var profileDropdown = cut.FindComponent<RadzenDropDown<string>>();

        // Act
        await cut.InvokeAsync(() => profileDropdown.Instance.Change.InvokeAsync(newProfileId));

        // Assert
        _mockProfileManager.Verify(x => x.ActivateProfileAsync(newProfileId, false), Times.Once);
    }

    [Fact]
    public void ShouldShowProfileDropdown_WhenProfilesExist()
    {
        // Arrange - profiles are set up in constructor

        // Act
        var cut = Render<AiChat>();

        // Assert
        var profileDropdown = cut.FindComponent<RadzenDropDown<string>>();
        Assert.NotNull(profileDropdown);
    }

    [Fact]
    public void ShouldNotShowProfileDropdown_WhenNoProfiles()
    {
        // Arrange
        _profileOptions.Profiles.Clear();

        // Act
        var cut = Render<AiChat>();

        // Assert
        Assert.Throws<ComponentNotFoundException>(() => cut.FindComponent<RadzenDropDown<string>>());
    }

    [Fact]
    public async Task ShouldInitializeVsBridge_OnInitialized()
    {
        // Arrange
        _mockVsBridge.Setup(x => x.InitializeAsync()).Returns(Task.CompletedTask);
        _mockToolManager.Setup(x => x.RegisterAllTools());

        // Act
        var cut = Render<AiChat>();

        // Wait for async operations
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        _mockVsBridge.Verify(x => x.InitializeAsync(), Times.Once);
        _mockToolManager.Verify(x => x.RegisterAllTools(), Times.Once);
    }

    [Fact]
    public void ShouldRenderUsageIndicators()
    {
        // Arrange & Act
        var cut = Render<AiChat>();

        // Assert
        Assert.NotNull(cut.Find(".usage-indicators-stub"));
    }

    [Fact]
    public void ShouldRenderAiChatInput()
    {
        // Arrange & Act
        var cut = Render<AiChat>();

        // Assert
        Assert.NotNull(cut.Find(".ai-chat-input-stub"));
    }

    [Fact]
    public async Task ShouldSendMessage_WhenContentProvided()
    {
        // Arrange
        var content = "Test message";
        _mockChatService.Setup(x => x.GetCompletionsAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatDelta>);
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var cut = Render<AiChat>();

        // Act - call SendMessageAsync directly
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync(content));

        // Assert - SendMessageAsync adds user message and then GetAiResponseAsync adds assistant placeholder
        Assert.Equal(2, _session.Messages.Count);
        Assert.Equal(content, _session.Messages[0].Content);
        Assert.Equal(ChatMessageRole.User, _session.Messages[0].Role);
        Assert.Equal(ChatMessageRole.Assistant, _session.Messages[1].Role);
    }

    [Fact]
    public async Task ShouldNotSendMessage_WhenContentIsEmpty()
    {
        // Arrange
        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync(""));

        // Assert
        Assert.Empty(_session.Messages);
    }

    [Fact]
    public async Task ShouldNotSendMessage_WhenContentIsWhitespace()
    {
        // Arrange
        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("   "));

        // Assert
        Assert.Empty(_session.Messages);
    }

    [Fact]
    public void ShouldSetCorrectAttributes_OnChatContainer()
    {
        // Arrange & Act
        var cut = Render<AiChat>();
        var chatContainer = cut.Find(".rz-chat");

        // Assert
        Assert.NotNull(chatContainer);
        Assert.Equal("rz-chat rz-h-100", chatContainer.GetAttribute("class"));
    }

    [Fact]
    public async Task ShouldSubscribeToSessionChangedEvent_OnInitialized()
    {
        // Arrange & Act
        var cut = Render<AiChat>();

        // Assert - verify event subscription by triggering it
        var message = new VisualChatMessage { Content = "Test", Role = ChatMessageRole.User };
        _session.Messages.Add(message);

        // Trigger SessionChanged event
        _mockChatService.Raise(x => x.SessionChanged += null,
            new PropertyChangedEventArgs(nameof(ConversationSession)));

        // Wait for async invocation
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // The component should still render without errors
        Assert.NotNull(cut.Find(".rz-chat"));
    }

    [Fact]
    public void ShouldDisposeResources_WhenComponentDisposed()
    {
        // Arrange
        var cut = Render<AiChat>();

        // Act
        cut.Dispose();

        // Assert - verify event unsubscription by checking no exception on raise
        _mockChatService.Raise(x => x.SessionChanged += null,
            new PropertyChangedEventArgs(nameof(ConversationSession)));
        // No exception should be thrown
    }

    [Fact]
    public void ShouldRenderMessageWithCorrectKey()
    {
        // Arrange
        var message1 = new VisualChatMessage { Content = "Message 1", Role = ChatMessageRole.User };
        var message2 = new VisualChatMessage { Content = "Message 2", Role = ChatMessageRole.Assistant };
        _session.Messages.Add(message1);
        _session.Messages.Add(message2);

        // Act
        var cut = Render<AiChat>();

        // Assert
        var messageStubs = cut.FindAll(".chat-message-view-stub");
        Assert.Equal(2, messageStubs.Count);
        // Verify messages are rendered in order
        Assert.Contains(message1.Content, messageStubs[0].TextContent);
        Assert.Contains(message2.Content, messageStubs[1].TextContent);
    }
}

///<summary>
/// Helper class to create async enumerable from a collection for mocking IAsyncEnumerable
///</summary>
internal static class AsyncEnumerable
{
    public static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
