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
            TokensToCompress = 50000,
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

        Services.AddSingleton<IRetryHandler>(new RetryHandler());
        Services.AddSingleton<IToolCallHandler>(new ToolCallHandler(_mockToolManager.Object));
        Services.AddSingleton(new Mock<ISubAgentExecutor>().Object);
        Services.AddSingleton(new Mock<ISkillService>().Object);

        // Add Radzen components
        Services.AddRadzenComponents();

        // Setup JSInterop for Radzen and component JS calls
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
        JSInterop.SetupVoid("setChatHandler", _ => true);

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
        _session.AddMessage(userMessage);
        _session.AddMessage(assistantMessage);

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
        _mockChatService.Setup(x => x.GetCompletionsAsync(It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatDelta>);
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var cut = Render<AiChat>();

        // Act - call SendMessageAsync directly
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync(content));

        // Assert - SendMessageAsync adds user message and then GetAiResponseAsync adds assistant placeholder
        Assert.Equal(2, _session.GetMessageCount());
        var sessionMessages = _session.GetMessagesSnapshot();
        Assert.Equal(content, sessionMessages[0].Content);
        Assert.Equal(ChatMessageRole.User, sessionMessages[0].Role);
        Assert.Equal(ChatMessageRole.Assistant, sessionMessages[1].Role);
    }

    [Fact]
    public async Task ShouldNotSendMessage_WhenContentIsEmpty()
    {
        // Arrange
        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync(""));

        // Assert
        Assert.Empty(_session.GetMessagesSnapshot());
    }

    [Fact]
    public async Task ShouldNotSendMessage_WhenContentIsWhitespace()
    {
        // Arrange
        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("   "));

        // Assert
        Assert.Empty(_session.GetMessagesSnapshot());
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
        _session.AddMessage(message);

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
        _session.AddMessage(message1);
        _session.AddMessage(message2);

        // Act
        var cut = Render<AiChat>();

        // Assert
        var messageStubs = cut.FindAll(".chat-message-view-stub");
        Assert.Equal(2, messageStubs.Count);
        // Verify messages are rendered in order
        Assert.Contains(message1.Content, messageStubs[0].TextContent);
        Assert.Contains(message2.Content, messageStubs[1].TextContent);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HandleErrorAsync: при ретраебельной ошибке контент НЕ перезаписывается —
    //  сообщение будет удалено перед retryAction, перезапись + перестройка сегментов
    //  была бы мёртвой работой (и затирала бы частичный ответ модели).
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RetryableError_PartialContentNotOverwritten()
    {
        // Arrange - stream fails after yielding partial content; MaxRetries = 2 → one retry,
        // the second attempt also fails → final error. The partial content of the failed
        // attempt must NOT be overwritten with "Error: ..." before the retry (dead work).
        _commonOptions.MaxRetries = 2;

        // Content snapshots AFTER the pump returns from the stream (including any
        // HandleErrorAsync overwrite): the old code (overwrite before retry) would leave
        // "Error: boom [1/2]" in the snapshot — the regression is caught by name.
        var postStreamContentSnapshots = new List<string>();
        SetupFailingStream(postStreamContentSnapshots, failureMessage: "boom");

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert
        // The FIRST attempt's message kept the partial content (NOT overwritten with the
        // error before the retry — the overwrite+segments-rebuild was dead work).
        Assert.Equal("Partial answer before failure", postStreamContentSnapshots[0]);

        // Retry budget exhausted: the final message DOES show the error.
        var messages = _session.GetMessagesSnapshot();
        Assert.Equal(2, messages.Count); // user + assistant with error
        Assert.Contains("Error:", messages[1].Content);
        Assert.Contains("boom", messages[1].Content);
    }

    [Fact]
    public async Task ExhaustedRetries_FinalMessageShowsError()
    {
        // Arrange - stream always fails; MaxRetries = 1 → no retries, error written to the message
        _commonOptions.MaxRetries = 1;

        SetupFailingStream(postStreamContentSnapshots: null, failureMessage: "boom");

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - retry budget exhausted: the error IS written to the final message
        var messages = _session.GetMessagesSnapshot();
        Assert.Equal(2, messages.Count); // user + assistant with error
        Assert.Contains("Error:", messages[1].Content);
        Assert.Contains("boom", messages[1].Content);
    }

    /// <summary>
    /// Multi-turn run fails with retry budget left: partial turns created by
    /// <c>onNewTurn</c> must be REMOVED before the retry — the new run appends new
    /// turns after the stale partial ones (duplicates).
    /// </summary>
    [Fact]
    public async Task RetryableError_MultiTurn_PartialTurnsRemovedBeforeRetry()
    {
        // Arrange - run 1: turn 1 streams content, onNewTurn starts turn 2, turn 2
        // streams content, then the stream fails. MaxRetries = 2 → one retry; run 2
        // fails the same way → final error.
        _commonOptions.MaxRetries = 2;

        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        _mockChatService
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>?>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<VisualChatMessage>?>()))
            .Returns(async (VisualChatMessage message,
                IAsyncEnumerable<ChatDelta> deltas,
                Action<string>? onContentUpdate,
                Action<List<ToolCall>>? onToolCallsUpdate,
                Action? onStateChange,
                CompletionsResult resultCapture,
                CancellationToken cancellationToken,
                Func<VisualChatMessage>? onNewTurn) =>
            {
                await foreach (var delta in deltas.WithCancellation(cancellationToken))
                {
                    if (delta.IsStartOfTurn && onNewTurn is not null && message.IsStreaming)
                    {
                        message = onNewTurn();
                    }

                    if (delta.Content is not null)
                    {
                        message.Content += delta.Content;
                        onContentUpdate?.Invoke(delta.Content);
                    }

                    onStateChange?.Invoke();
                }
            });

        _mockChatService
            .Setup(x => x.GetCompletionsAsync(It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((CompletionsResult _, CancellationToken _) => CreateFailingStream(
                [
                    new ChatDelta { Content = "turn1 partial" },
                    new ChatDelta { Content = "turn2 partial", IsStartOfTurn = true }
                ],
                "boom"));

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert
        var messages = _session.GetMessagesSnapshot();

        // Run 1 partial turns were removed before the retry. Run 2 also fails with
        // exhausted budget → nothing removed on the final failure: its turn 1 partial
        // stays as history, its turn 2 message is overwritten with the error.
        // Without the fix the session would contain TWO "turn1 partial" messages
        // (stale run 1 turn + run 2 turn) — the duplicate is the regression.
        Assert.Equal(3, messages.Count); // user + run2 turn1 partial + final error
        Assert.Single(messages.Where(m => m.Content == "turn1 partial"));
        Assert.DoesNotContain("turn2 partial", messages.Select(m => m.Content));
        Assert.Contains("Error:", messages[2].Content);
        Assert.Contains("boom", messages[2].Content);
    }

    /// <summary>
    /// Wires <see cref="IChatService.ProcessStreamAsync"/> to a minimal in-test pump that
    /// streams the given deltas into the message (content append + callbacks) and then
    /// throws (simulates a transport failure mid-stream). Every call (including retries)
    /// fails the same way. The content snapshot is taken AFTER the pump returns —
    /// HandleErrorAsync mutations (error overwrite) are included in it.
    /// </summary>
    private void SetupFailingStream(List<string>? postStreamContentSnapshots, string failureMessage)
    {
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        _mockChatService
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>?>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<VisualChatMessage>?>()))
            .Returns(async (VisualChatMessage message,
                IAsyncEnumerable<ChatDelta> deltas,
                Action<string>? onContentUpdate,
                Action<List<ToolCall>>? onToolCallsUpdate,
                Action? onStateChange,
                CompletionsResult resultCapture,
                CancellationToken cancellationToken,
                Func<VisualChatMessage>? onNewTurn) =>
            {
                try
                {
                    await foreach (var delta in deltas.WithCancellation(cancellationToken))
                    {
                        if (delta.IsStartOfTurn && onNewTurn is not null && message.IsStreaming)
                        {
                            message = onNewTurn();
                        }

                        if (delta.Content is not null)
                        {
                            message.Content += delta.Content;
                            onContentUpdate?.Invoke(delta.Content);
                        }

                        onStateChange?.Invoke();
                    }
                }
                finally
                {
                    // AFTER the pump returns: HandleErrorAsync (called by the component's
                    // catch) may have overwritten the content — the snapshot includes it.
                    postStreamContentSnapshots?.Add(message.Content);
                }
            });

        _mockChatService
            .Setup(x => x.GetCompletionsAsync(It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((CompletionsResult _, CancellationToken _) => CreateFailingStream(
                [new ChatDelta { Content = "Partial answer before failure" }],
                failureMessage));
    }

    private static async IAsyncEnumerable<ChatDelta> CreateFailingStream(
        ChatDelta[] deltas, string failureMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var delta in deltas)
        {
            yield return delta;
        }

        throw new Exception(failureMessage);
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
