using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.JSInterop.Infrastructure;

namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="AiChat"/>
/// </summary>
public class AiChatTests : BunitContext
{
    private static int _runCount;
    private readonly Mock<IChatService> _mockChatService;
    private readonly Mock<IToolManager> _mockToolManager;
    private readonly Mock<IProfileManager> _mockProfileManager;
    private readonly Mock<ICommonSettingsProvider> _mockCommonSettings;
    private readonly Mock<IVsBridge> _mockVsBridge;
    private readonly Mock<IMessageParser> _mockMessageParser;
    private readonly Mock<IJSRuntime> _mockJsRuntime;
    private readonly Mock<IToolCallHandler> _mockToolCallHandler;
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
        _mockToolCallHandler = new Mock<IToolCallHandler>();

        // The turn loop owns the tool batch: approvals are prepared once and the batch runs
        // through the handler exactly once. Verified by the tests below.
        _mockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockToolCallHandler
            .Setup(x => x.HandleApprovalAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

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
        Services.AddSingleton(new UiErrorLogger(new Mock<IJSRuntime>().Object));
        Services.AddSingleton(new Mock<ILogger<AiChat>>().Object);

        Services.AddSingleton<IRetryHandler>(new RetryHandler());
        Services.AddSingleton(_mockToolCallHandler.Object);
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
        // IsLoading is projected into the DOM so a test can assert that the parent painted the
        // busy state BEFORE the first token (the Cancel button lives inside this child).
        ComponentFactories.AddStub<AiChatInput>(parameters => builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "ai-chat-input-stub");
                builder.AddAttribute(2, "data-loading", parameters.Get(p => p.IsLoading) ? "true" : "false");
                builder.AddContent(3, "AiChatInput Stub");
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
        // Arrange - the turn produces no payload at all
        var content = "Test message";
        _mockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatDelta>);
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var cut = Render<AiChat>();

        // Act - call SendMessageAsync directly
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync(content));

        // Assert - the user message is appended, and a turn WITHOUT payload leaves no
        // empty assistant bubble behind (lazy turn message).
        Assert.Equal(1, _session.GetMessageCount());
        var sessionMessages = _session.GetMessagesSnapshot();
        Assert.Equal(content, sessionMessages[0].Content);
        Assert.Equal(ChatMessageRole.User, sessionMessages[0].Role);
    }

    [Fact]
    public async Task SendMessageAsync_WhileStreaming_InterjectAddedWithoutNewRun()
    {
        // Arrange: the turn streams until released — IsLoading stays true.
        var release = new TaskCompletionSource();
        _mockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession _, VisualChatMessage? _, IReadOnlyList<Tool>? _, CompletionsResult capture, CancellationToken ct)
                => StreamUntilReleased(release, capture, ct));
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Start the run (IsLoading = true, interject window open).
        var firstRun = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("start"));

        // Wait until the first delta is consumed — guarantees the interject window is open
        // (_acceptInterjects is set before ProcessStreamAsync starts consuming).
        await WaitUntilAsync(() => _session.GetMessagesSnapshot()[^1].Content == "streaming...");

        // Act: send a second message while the run is streaming — it must be ADDED to the
        // session right after the streaming message (interject), NOT silently dropped and
        // NOT followed by a new assistant placeholder (no new run).
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("interject"));

        var snapshot = _session.GetMessagesSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(ChatMessageRole.User, snapshot[^1].Role);
        Assert.Equal("interject", snapshot[^1].Content);
        Assert.Equal(1, _runCount); // GetTurnCompletionsAsync called exactly once (no new run)

        // Finish the run.
        release.TrySetResult();
        await firstRun;
    }

    [Fact]
    public async Task SendMessageAsync_DuringCompression_MessageRejected()
    {
        // Arrange: the compact command sets IsLoading = true and runs compression —
        // the interject window is closed (_acceptInterjects is only open for the agent loop).
        // ProcessStreamAsync blocks until released, so IsLoading stays true.
        var release = new TaskCompletionSource();
        _mockChatService.Setup(x => x.CompressSessionAsync(It.IsAny<ConversationSession>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatDelta>);
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        _mockChatService.Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(release.Task);

        var cut = Render<AiChat>();
        var compactRun = cut.InvokeAsync(async () => await cut.Instance.HandleCommandAsync("compact"));

        // Wait until the compression assistant message is added (IsLoading = true).
        await WaitUntilAsync(() => _session.GetMessageCount() >= 1);

        // Act: a message sent during compression must be REJECTED — compression rewrites
        // the history from a snapshot and would silently drop the interject.
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("during compression"));

        var snapshot = _session.GetMessagesSnapshot();
        Assert.Single(snapshot);
        Assert.Equal(ChatMessageRole.Assistant, snapshot[^1].Role);
        Assert.False(snapshot[^1].Content == "during compression");

        release.TrySetResult();
        await compactRun;
    }

    /// <summary>
    /// Local variant of <see cref="StreamUntilReleased"/> without the static run counter:
    /// the static _runCount is asserted by other tests in this class.
    /// </summary>
    private static async IAsyncEnumerable<ChatDelta> StreamUntilReleasedLocal(
        TaskCompletionSource release, CompletionsResult capture, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ChatDelta { Content = "streaming..." };
        await release.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Regression for the cached-cancelled-token bug: cancel during a run, then send a
    /// message. The old code used the run's _cts.Token for the UI scroll (ScrollToBottomAsync):
    /// after cancellation the token stayed cancelled (recreated only inside
    /// GetAiResponseAsync, which never ran), so SendMessageAsync threw
    /// OperationCanceledException between AddMessage and GetAiResponseAsync — the message
    /// appeared in the chat but was never sent to the agent and never saved to localStorage.
    /// </summary>
    [Fact]
    public async Task SendMessageAsync_AfterCancel_NewRunStartsAndSessionSaved()
    {
        // Arrange: run 1 streams until released (cancel target).
        var release = new TaskCompletionSource();
        var runCount = 0;
        // Local stream factory: does NOT touch the static _runCount that
        // SendMessageAsync_WhileStreaming_InterjectAddedWithoutNewRun asserts on.
        _mockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession _, VisualChatMessage? _, IReadOnlyList<Tool>? _, CompletionsResult capture, CancellationToken ct) =>
            {
                Interlocked.Increment(ref runCount);
                capture.Model = "test-model";
                return StreamUntilReleasedLocal(release, capture, ct);
            });
        var saveCount = 0;
        _mockChatService.Setup(x => x.SaveSessionAsync())
            .Callback(() => Interlocked.Increment(ref saveCount))
            .Returns(Task.CompletedTask);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var firstRun = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("start"));
        await WaitUntilAsync(() => _session.GetMessagesSnapshot()[^1].Content == "streaming...");

        // Act 1: cancel (the same path as the Cancel button in AiChatInput).
        var cancelMethod = typeof(AiChat).GetMethod("CancelResponseAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await cut.InvokeAsync(async () => await (Task)cancelMethod!.Invoke(cut.Instance, null)!);
        await WaitUntilAsync(() => _session.GetMessagesSnapshot().All(m => !m.IsStreaming));

        // Release the gate AFTER run 1 fully unwound: the second run reuses the same
        // stream factory, so an unreleased gate would block the new run forever.
        release.TrySetResult();
        await firstRun;

        // Act 2: a message after cancel must NOT throw on the cancelled scroll token.
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("after cancel"));

        // Assert: a new run started, the message is in the session, the session was saved.
        Assert.Equal(2, runCount);
        Assert.Contains(_session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.User && m.Content == "after cancel");
        Assert.True(saveCount > 0);
    }

    /// <summary>
    /// Compression rewrites the history in the MIDDLE of the chat. The user keeps looking at the
    /// bottom (a mid-run compression happens while the run still streams), so the view must NOT be
    /// yanked to the summary. Instead: a badge while it runs, a notification when it is done, and
    /// the summary is revealed only when the user clicks that notification.
    /// </summary>
    [Fact]
    public async Task Compression_ShowsBadgeAndNotification_ButDoesNotStealTheScroll()
    {
        // Arrange
        var notificationService = Services.GetRequiredService<NotificationService>();
        var summary = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "## Goal\n- summary" };
        _session.AddMessage(summary);

        // AiChat injects IJSRuntime (mocked in this fixture), so the scroll call is observed on
        // the mock — bUnit's JSInterop is not in that path.
        var scrollCalls = new List<object?[]>();
        _mockJsRuntime
            .Setup(js => js.InvokeAsync<IJSVoidResult>("scrollToMessage", It.IsAny<CancellationToken>(), It.IsAny<object?[]>()))
            .Callback<string, CancellationToken, object?[]>((_, _, args) => scrollCalls.Add(args))
            .Returns(ValueTask.FromResult<IJSVoidResult>(null!));

        var cut = Render<AiChat>();

        // Act 1: compression started mid-run — the chat would otherwise just look frozen.
        await cut.InvokeAsync(() =>
        {
            _mockChatService.Raise(x => x.CompressionStarted += null, _session);
            return Task.CompletedTask;
        });

        Assert.Contains(SharedResource.Compressing, cut.Markup);

        // Act 2: compression finished — history rewritten, summary sits in the middle.
        await cut.InvokeAsync(() =>
        {
            _mockChatService.Raise(x => x.CompressionCompleted += null,
                new CompressionReport(_session, summary, 4));
            return Task.CompletedTask;
        });

        // Assert: badge gone, notification explains what happened...
        Assert.DoesNotContain(SharedResource.Compressing, cut.Markup);
        var notification = Assert.Single(notificationService.Messages);
        Assert.Equal(SharedResource.ContextCompressed, notification.Summary);
        Assert.Equal(summary.Id, notification.Payload);

        // ...and the chat did NOT move: no forced scroll to the middle of the history.
        Assert.Empty(scrollCalls);

        // Act 3: the user clicks the notification — now (and only now) the summary is revealed.
        // The click is wired as a Radzen callback; invoke the handler directly (the callback's
        // own type is an implementation detail of the library).
        var handler = typeof(AiChat).GetMethod("OnCompressionNotificationClick",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await cut.InvokeAsync(() =>
        {
            handler!.Invoke(cut.Instance, [new NotificationMessage { Payload = summary.Id }]);
            return Task.CompletedTask;
        });

        await WaitUntilAsync(() => scrollCalls.Count > 0);
        var args = Assert.Single(scrollCalls);
        Assert.Equal(summary.Id, args[0] as string);
    }

    [Fact]
    public async Task CompressionCompleted_ForAnotherSession_IsIgnored()
    {
        // Arrange: a sub-agent branch compresses its own session — the main chat must not notify
        // about it (the branch view has its own indicator).
        var notificationService = Services.GetRequiredService<NotificationService>();
        var branchSession = new ConversationSession { Id = "subagent" };
        var summary = new VisualChatMessage { Role = ChatMessageRole.Assistant, Content = "summary" };

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(() =>
        {
            _mockChatService.Raise(x => x.CompressionCompleted += null,
                new CompressionReport(branchSession, summary, 3));
            return Task.CompletedTask;
        });

        // Assert
        Assert.Empty(notificationService.Messages);
    }

    private static async IAsyncEnumerable<ChatDelta> StreamUntilReleased(
        TaskCompletionSource release, CompletionsResult capture, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _runCount);
        capture.Model = "test-model";
        yield return new ChatDelta { Content = "streaming..." };
        await release.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Mocks <see cref="IChatService.ProcessStreamAsync"/> with the REAL semantics of one turn:
    /// content/reasoning applied to the message, tool calls mirrored into the capture, the
    /// message closed in a <c>finally</c> (also on cancellation/failure). One stream = one turn.
    /// The run stays active while the stream blocks on a release gate.
    /// </summary>
    private void SetupProcessStreamConsume()
    {
        _mockChatService.Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (VisualChatMessage msg, IAsyncEnumerable<ChatDelta> deltas,
                Action<string>? onContent, Action<List<ToolCall>>? onTools, Action? onState,
                CompletionsResult capture, CancellationToken ct) =>
            {
                msg.IsStreaming = true;
                try
                {
                    await foreach (var delta in deltas.WithCancellation(ct))
                    {
                        if (delta.Content is not null)
                        {
                            msg.Content += delta.Content;
                            onContent?.Invoke(delta.Content);
                        }

                        if (delta.ToolCalls is { Count: > 0 })
                        {
                            // AfDeltaMapper parity: the accumulated calls flow through the capture.
                            capture.AccumulatedToolCalls = [.. delta.ToolCalls];
                            msg.ToolCalls = capture.AccumulatedToolCalls;
                            onTools?.Invoke(capture.AccumulatedToolCalls);
                        }

                        onState?.Invoke();
                    }
                }
                finally
                {
                    msg.IsStreaming = false;
                }
            });
    }

    /// <summary>Runs the private <c>CancelResponseAsync</c> (the Cancel button path) on a rendered cut.</summary>
    private static async Task CancelRunAsync(AiChat instance)
    {
        var cancelMethod = typeof(AiChat).GetMethod("CancelResponseAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        await (Task)cancelMethod!.Invoke(instance, null)!;
    }

    [Fact]
    public async Task TurnLoop_CompressionBetweenTurns_NextTurnRequestIsCompacted()
    {
        // The headline defect (todo.md line 3): the UI showed a compressed session while the
        // RUN kept sending the uncompressed history. The wire history is now rebuilt from the
        // session at the start of every turn, so compaction between turns must reach the model.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        // The service owns the accounting: the threshold is "crossed" once turn 1 exists.
        _mockChatService
            .Setup(x => x.NeedsCompression(It.IsAny<ConversationSession>()))
            .Returns((ConversationSession session) =>
                session.GetMessagesSnapshot().Any(m => m.Content == "turn1"));

        // Real compaction as the service performs it: the history is rewritten IN PLACE.
        _mockChatService
            .Setup(x => x.CompressSessionAsync(It.IsAny<ConversationSession>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession session, CompletionsResult _, CancellationToken _) =>
            {
                var toCollapse = session.GetMessagesSnapshot()
                    .Where(m => m.Role != ChatMessageRole.System)
                    .ToList();
                session.CollapseMessages(toCollapse, new VisualChatMessage
                {
                    Role = ChatMessageRole.Assistant,
                    Content = "SUMMARY"
                });
                return AsyncEnumerable.Empty<ChatDelta>();
            });

        var toolCall = CreateToolCall("call_1", "read_file");
        List<VisualChatMessage>? historyAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                historyAtTurn2 = [.. _session.GetMessagesSnapshot()];
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The compaction ran exactly once, BETWEEN the turns.
        _mockChatService.Verify(x => x.CompressSessionAsync(It.IsAny<ConversationSession>(),
            It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()), Times.Once);

        // Turn 2 was requested from the COMPACTED history: the pre-compression turn is gone,
        // the summary travels instead.
        Assert.NotNull(historyAtTurn2);
        Assert.Contains(historyAtTurn2!, m => m.Content == "SUMMARY");
        Assert.DoesNotContain(historyAtTurn2!, m => m.Content == "turn1");
    }

    [Fact]
    public async Task TurnLoop_MessageEditedBetweenTurns_NextTurnSeesEditedContent()
    {
        // An edit applied between the turns (there was NO compensation for this before: the
        // wire history was frozen for the whole run).
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");

        _mockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> _, AppMode _, CancellationToken _) =>
            {
                // Between the turns: the user edits the original request.
                var userMessage = _session.GetMessagesSnapshot().First(m => m.Role == ChatMessageRole.User);
                _session.UpdateMessage(userMessage.Id, "edited request");
                return Task.CompletedTask;
            });

        List<VisualChatMessage>? historyAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                historyAtTurn2 = [.. _session.GetMessagesSnapshot()];
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The second turn saw the edited text, not the original one.
        Assert.NotNull(historyAtTurn2);
        Assert.Contains(historyAtTurn2!, m => m.Content == "edited request");
        Assert.DoesNotContain(historyAtTurn2!, m => m.Content == "hi");
    }

    [Fact]
    public async Task TurnLoop_MessageDeletedBetweenTurns_NextTurnOmitsIt()
    {
        // A deletion applied between the turns (also without compensation before the refactor).
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");

        _mockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> _, AppMode _, CancellationToken _) =>
            {
                var userMessage = _session.GetMessagesSnapshot().First(m => m.Role == ChatMessageRole.User);
                _session.RemoveMessage(userMessage.Id);
                return Task.CompletedTask;
            });

        List<VisualChatMessage>? historyAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                historyAtTurn2 = [.. _session.GetMessagesSnapshot()];
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The deleted message is gone from the second request.
        Assert.NotNull(historyAtTurn2);
        Assert.DoesNotContain(historyAtTurn2!, m => m.Content == "hi");
    }

    [Fact]
    public async Task TurnLoop_ModeSwitchedBetweenTurns_SecondTurnUsesNewMode()
    {
        // switch_mode must affect the NEXT turn: the system prompt and the tool set are derived
        // from the session mode when the request is built.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", BasicEnum.SwitchMode);

        _mockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> _, AppMode _, CancellationToken _) =>
            {
                _session.Mode = AppMode.Plan;
                return Task.CompletedTask;
            });

        AppMode? modeAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                modeAtTurn2 = _session.Mode;
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The second turn ran under the NEW mode.
        Assert.Equal(AppMode.Plan, modeAtTurn2);
        _mockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task TurnLoop_InterjectBetweenTurns_ReachesModel()
    {
        // A message written BETWEEN the turns (while the tool batch runs) must be answered:
        // afterwards the run continues with another turn that carries it.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        _mockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns((List<ToolCall> _, AppMode _, CancellationToken _) =>
            {
                _session.AddMessage(VisualChatMessage.CreateUser("also check the tests"));
                return Task.CompletedTask;
            });

        List<VisualChatMessage>? historyAtTurn2 = null;
        SetupTurnScripts(
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }], ct),
            (_, ct) =>
            {
                historyAtTurn2 = [.. _session.GetMessagesSnapshot()];
                return StreamDeltasAsync([new ChatDelta { Content = "turn2" }], ct);
            });
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The interject reaches the model in the second turn.
        Assert.NotNull(historyAtTurn2);
        Assert.Contains(historyAtTurn2!, m => m.Content == "also check the tests");
        _mockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static ToolCall CreateToolCall(string id, string name, string arguments = "{}")
        => new() { Id = id, Function = new ToolCallFunction { Name = name, Arguments = arguments } };

    /// <summary>
    /// Scripts one stream per turn (the LAST script repeats for any further turn). Linearly
    /// enumerable sequences: a turn script may throw after yielding to simulate a transport
    /// failure mid-turn.
    /// </summary>
    private void SetupTurnScripts(params Func<CompletionsResult, CancellationToken, IAsyncEnumerable<ChatDelta>>[] turns)
    {
        var call = 0;
        _mockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession _, VisualChatMessage? _, IReadOnlyList<Tool>? _, CompletionsResult capture, CancellationToken ct) =>
            {
                var index = Interlocked.Increment(ref call) - 1;
                capture.Model = "test-model";
                if (turns.Length == 0)
                    return AsyncEnumerable.Empty<ChatDelta>(); // a turn without payload

                return turns[Math.Min(index, turns.Length - 1)](capture, ct);
            });
    }

    /// <summary>Shortcut for <see cref="SetupTurnScripts"/>: a scripted content stream per turn.</summary>
    private void SetupTurnStreams(params ChatDelta[][] turns)
        => SetupTurnScripts([.. turns.Select<ChatDelta[], Func<CompletionsResult, CancellationToken, IAsyncEnumerable<ChatDelta>>>(
            deltas => (_, ct) => StreamDeltasAsync(deltas, ct))]);

    private static async IAsyncEnumerable<ChatDelta> StreamDeltasAsync(ChatDelta[] deltas, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var delta in deltas)
        {
            ct.ThrowIfCancellationRequested();
            yield return delta;
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// A stream that delivers its deltas and then fails the way the REAL transport fails.
    /// <para>
    /// The exception type matters: retry eligibility is decided by
    /// <see cref="LlmApiException.IsRetryable"/>, and a generic exception means "a bug in our own
    /// code" — which is deliberately NOT retried. Production never surfaces a raw
    /// <c>InvalidOperationException</c> from the LLM path: <c>AfCancelRace</c> wraps every
    /// transport failure into <see cref="LlmApiException"/>. A test that threw a plain
    /// <c>Exception</c> here would assert a retry path the application cannot reach.
    /// </para>
    /// </summary>
    private static async IAsyncEnumerable<ChatDelta> StreamThenThrowAsync(
        ChatDelta[] deltas, string failureMessage, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var delta in deltas)
        {
            yield return delta;
        }

        await Task.CompletedTask;
        throw new LlmApiException(failureMessage);
    }

    private static async IAsyncEnumerable<ChatDelta> BlockedStream(
        TaskCompletionSource release, [EnumeratorCancellation] CancellationToken ct)
    {
        await release.Task.WaitAsync(ct);
        yield break;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail("Condition was not met within the timeout");
            await Task.Delay(10);
        }
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
        Assert.True(true);
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
        // user + the technical note of the retried attempt + assistant with error.
        Assert.Equal(3, messages.Count);

        // The retried attempt did not vanish silently: a technical note records it in place of
        // the removed turn. It is a trace, never context — BuildWire must not send it.
        var note = Assert.Single(messages, m => m.IsTechnical);
        Assert.Contains("Retry 1/2", note.Content);
        Assert.Contains("boom", note.Content);
        var wireContents = _session.BuildWire("sys").Messages.Select(m => m.Content ?? string.Empty).ToList();
        Assert.DoesNotContain(note.Content, wireContents);

        Assert.Contains("Error:", messages[2].Content);
        Assert.Contains("boom", messages[2].Content);
    }

    /// <summary>
    /// A permanent failure is reported on the FIRST attempt. Before the classification existed
    /// the user waited through the whole budget (~19 minutes with the defaults) to be told about a
    /// wrong API key — an answer that never changed and was knowable immediately.
    /// </summary>
    [Fact]
    public async Task PermanentFailure_NotRetried_ReportedImmediately()
    {
        // Arrange: plenty of budget, but the failure cannot succeed on a repeat.
        _commonOptions.MaxRetries = 10;
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupTurnScripts((_, ct) => ThrowLlmAsync(
            LlmApiException.ForHttpStatus(401, "HttpCode: 401 | invalid api key"), ct));
        // The pump is what enumerates the scripted stream — without it the failure never surfaces.
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert: exactly ONE turn was requested.
        _mockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // No retry badge was armed, and no countdown was shown for an attempt that never happened.
        var messages = _session.GetMessagesSnapshot();
        Assert.Equal(2, messages.Count); // user + error, no technical note
        Assert.DoesNotContain(messages, m => m.IsTechnical);

        // The counter is omitted: "[1/10]" would claim nine attempts that will not happen.
        Assert.Contains("Error:", messages[1].Content);
        Assert.Contains("401", messages[1].Content);
        Assert.DoesNotContain("/10]", messages[1].Content);
    }

    /// <summary>
    /// An exception that is not an <see cref="LlmApiException"/> means a bug in our own code (the
    /// transport wraps everything it produces). Retrying it hides the bug behind minutes of
    /// waiting; it must surface at once with its original type preserved in the log.
    /// </summary>
    [Fact]
    public async Task NonLlmException_NotRetried_ReportedImmediately()
    {
        _commonOptions.MaxRetries = 10;
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupTurnScripts((_, ct) => ThrowLlmAsync(new InvalidOperationException("bug in our code"), ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        _mockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Once);

        var messages = _session.GetMessagesSnapshot();
        Assert.Equal(2, messages.Count);
        Assert.Contains("bug in our code", messages[1].Content);
        Assert.DoesNotContain(messages, m => m.IsTechnical);
    }

    /// <summary>
    /// A retryable failure still spends the budget exactly as before: the classification only
    /// narrows what is retried, it never widens it.
    /// </summary>
    [Fact]
    public async Task RetryableFailure_StillConsumesBudget()
    {
        _commonOptions.MaxRetries = 2;
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        // No status: a timeout / transport failure — the cause is unknown, so the retry is kept.
        SetupTurnScripts((_, ct) => ThrowLlmAsync(new LlmApiException("Request timed out after 100s"), ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Two attempts (budget of 2) and one technical note left where the first turn was dropped.
        _mockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));

        var messages = _session.GetMessagesSnapshot();
        Assert.Single(messages, m => m.IsTechnical);
        Assert.Contains("Error:", messages[^1].Content);
        Assert.Contains("[2/2]", messages[^1].Content);
    }

    /// <summary>Fails the turn with the given exception, the way the real transport does.</summary>
    private static async IAsyncEnumerable<ChatDelta> ThrowLlmAsync(
        Exception exception, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // Unreachable: keeps this method an async iterator (throw on MoveNext).
        yield break;
#pragma warning restore CS0162
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
    /// A retryable failure of the LAST turn must not wipe the COMPLETED turns: only the partial
    /// turn is removed before the retry (it is never re-sent), so the retried turn sees a
    /// consistent wire history and the finished work stays in the session.
    /// </summary>
    [Fact]
    public async Task RetryableError_MultiTurn_CompletedTurnsKept_RetryContinues()
    {
        // Arrange - MaxRetries = 2 → exactly ONE retry of the failing turn.
        _commonOptions.MaxRetries = 2;
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        SetupTurnScripts(
            // Turn 1: text + a tool call → the turn completes, the loop continues.
            (_, ct) => StreamDeltasAsync([new ChatDelta { Content = "turn1 partial", ToolCalls = [toolCall] }], ct),
            // Turn 2 and every retry of it: partial content, then the transport dies.
            (_, ct) => StreamThenThrowAsync([new ChatDelta { Content = "turn2 partial" }], "boom", ct));

        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - turn 1 (completed) survives; the failed turn 2 was dropped before the retry
        // and replaced by the retry's message carrying the final error.
        _mockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3)); // turn 1 + turn 2 + one retry

        var messages = _session.GetMessagesSnapshot();
        // user + turn1 (kept) + technical note of the retried turn2 + turn2 (error).
        Assert.Equal(4, messages.Count);
        Assert.Single(messages, m => m.Content == "turn1 partial");
        Assert.DoesNotContain("turn2 partial", messages.Select(m => m.Content));

        // The retried turn left a note where it was dropped — after the completed turn 1.
        var note = Assert.Single(messages, m => m.IsTechnical);
        Assert.Contains("Retry 1/2", note.Content);
        Assert.Equal(2, messages.IndexOf(note));

        Assert.Contains("Error:", messages[3].Content);
        Assert.Contains("boom", messages[3].Content);
        Assert.All(messages, m => Assert.False(m.IsStreaming));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Turn loop: ONE GetTurnCompletionsAsync + ONE ProcessStreamAsync per turn,
    //  the turn message created lazily on the first payload.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TurnLoop_TwoTurns_ExactlyTwoCompletionsCalls_AndTwoAssistantMessages()
    {
        // Arrange - turn 1 asks for a tool (that is what continues the run), turn 2 answers.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        SetupTurnStreams(
            [new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }],
            [new ChatDelta { Content = "turn2" }]);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - exactly two LLM turns, no empty placeholder anywhere.
        _mockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));

        var messages = _session.GetMessagesSnapshot();
        Assert.Equal(3, messages.Count); // user + two assistant turns
        Assert.Equal(2, messages.Count(m => m.Role == ChatMessageRole.Assistant));
        Assert.DoesNotContain(messages, m => m.Role == ChatMessageRole.Assistant
                                             && string.IsNullOrEmpty(m.Content)
                                             && m.ToolCalls is not { Count: > 0 });
        Assert.Equal("turn1", messages[1].Content);
        Assert.Equal("turn2", messages[2].Content);
        Assert.All(messages, m => Assert.False(m.IsStreaming));
    }

    [Fact]
    public async Task TurnLoop_TurnWithoutPayload_LeavesNoMessage()
    {
        // Arrange - the turn produces nothing at all (no content, no reasoning, no tool calls)
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupTurnStreams([]);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - no payload, so the eagerly added bubble was removed again:
        // an empty turn leaves NO message behind.
        var messages = _session.GetMessagesSnapshot();
        Assert.Single(messages);
        Assert.Equal(ChatMessageRole.User, messages[0].Role);
    }

    [Fact(Timeout = 20000)]
    public async Task TurnLoop_BeforeFirstToken_ShowsPlaceholderAndCancelButton()
    {
        // The dead window: the request was accepted but the provider has not sent a single byte.
        // The UI looked frozen there - no bubble, no streaming indicator and no way to cancel -
        // because the turn message was only added on the first payload AND IsLoading was set
        // without a re-render.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupTurnScripts((_, ct) => BlockingStreamAsync(gate.Task, ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Wait for the run to reach the transport: nothing has been delivered at this point.
        await WaitUntilAsync(() => _session.GetMessagesSnapshot()
            .Any(m => m.Role == ChatMessageRole.Assistant));

        // The bubble exists, is empty and open for streaming.
        var placeholder = Assert.Single(_session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.Assistant);
        Assert.True(placeholder.IsStreaming);
        Assert.Equal(string.Empty, placeholder.Content);

        // It is on screen (the view itself is stubbed in bUnit: the session entry is rendered)...
        Assert.NotEmpty(cut.FindAll($"[data-message-id='{placeholder.Id}']"));

        // ...and the parent already painted the busy state, so the Cancel button (which lives in
        // the input child) is available while nothing has arrived.
        Assert.Equal("true", cut.Find(".ai-chat-input-stub").GetAttribute("data-loading"));

        gate.TrySetResult();
        await run;

        // A run that ends without payload still leaves no bubble behind.
        Assert.DoesNotContain(_session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.Assistant);
    }

    /// <summary>
    /// An update stream that delivers nothing until <paramref name="gate"/> completes: models a
    /// request whose first token is still pending.
    /// </summary>
    private static async IAsyncEnumerable<ChatDelta> BlockingStreamAsync(Task gate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        yield break;
    }

    [Fact]
    public async Task TurnLoop_CancelAfterFirstTurn_NoSecondCall_AllMessagesClosed()
    {
        // Arrange - turn 1 asks for a tool; the cancellation arrives while the batch runs.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        SetupTurnStreams(
            [new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }],
            [new ChatDelta { Content = "turn2" }]);
        SetupProcessStreamConsume();

        AiChat? instance = null;
        _mockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns(async () => await CancelRunAsync(instance!));

        var cut = Render<AiChat>();
        instance = cut.Instance;

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - ONE token source per run: the cancelled run never starts another turn and
        // every message it created is closed (otherwise it would be filtered out of the wire).
        _mockChatService.Verify(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(),
            It.IsAny<VisualChatMessage?>(), It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.All(_session.GetMessagesSnapshot(), m => Assert.False(m.IsStreaming));
    }

    [Fact(Timeout = 20000)]
    public async Task TurnLoop_CancelBeforeFirstToken_LeavesNoBubble()
    {
        // The turn message is added EAGERLY now (the bubble is the feedback that the run started).
        // Cancelling inside that window must not leave it behind: nothing arrived, so there is
        // nothing to show and nothing to send. Regression guard - the cancel handler writes
        // "Cancelled by user..." into an empty message, so a payload check placed AFTER it always
        // said "non-empty" and the bubble survived.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupTurnScripts((_, ct) => BlockingStreamAsync(gate.Task, ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // The run has reached the transport and nothing has been delivered yet.
        await WaitUntilAsync(() => _session.GetMessagesSnapshot()
            .Any(m => m.Role == ChatMessageRole.Assistant));

        // Act - the Cancel button path.
        await cut.InvokeAsync(async () => await CancelRunAsync(cut.Instance));
        gate.TrySetResult();
        await run;

        // Assert - only the user message survives, and it is not "Cancelled by user...".
        var messages = _session.GetMessagesSnapshot();
        Assert.Single(messages);
        Assert.Equal(ChatMessageRole.User, messages[0].Role);
        Assert.DoesNotContain("Cancelled by user", string.Join('|', messages.Select(m => m.Content)));
    }

    [Fact(Timeout = 20000)]
    public async Task TurnLoop_CancelAfterPartialContent_KeepsPartialAnswer()
    {
        // The counterpart: once the model produced text, cancelling keeps it. The user asked to
        // stop, not to discard what was already written - and the handler must not overwrite it.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupTurnScripts((_, ct) => PartialThenBlockAsync(gate.Task, ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        await WaitUntilAsync(() => _session.GetMessagesSnapshot()
            .Any(m => m.Content == "partial"));

        // Act
        await cut.InvokeAsync(async () => await CancelRunAsync(cut.Instance));
        gate.TrySetResult();
        await run;

        // Assert - the partial answer is intact and was not replaced by the cancel notice.
        var turn = Assert.Single(_session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.Assistant);
        Assert.Equal("partial", turn.Content);
        Assert.False(turn.IsStreaming);
    }

    [Fact(Timeout = 20000)]
    public async Task TurnLoop_CancelBeforeTokenWithOpenToolCalls_KeepsCallsAndFillsResults()
    {
        // Tool calls are payload: the wire history needs assistant(tool_calls) followed by a tool
        // message for EVERY call, so the bubble must stay and its results must be stubbed.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupTurnScripts((_, ct) => ToolCallThenBlockAsync(toolCall, gate.Task, ct));
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        await WaitUntilAsync(() => _session.GetMessagesSnapshot()
            .Any(m => m.ToolCalls is { Count: > 0 }));

        // Act
        await cut.InvokeAsync(async () => await CancelRunAsync(cut.Instance));
        gate.TrySetResult();
        await run;

        // Assert
        var turn = Assert.Single(_session.GetMessagesSnapshot(),
            m => m.Role == ChatMessageRole.Assistant);
        var call = Assert.Single(turn.ToolCalls!);
        Assert.NotNull(call.Result);
        Assert.False(call.Result!.Success);
    }

    /// <summary>Streams one content delta, then blocks until <paramref name="gate"/> completes.</summary>
    private static async IAsyncEnumerable<ChatDelta> PartialThenBlockAsync(Task gate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatDelta { Content = "partial" };
        await gate.WaitAsync(cancellationToken);
    }

    /// <summary>Streams one tool call, then blocks until <paramref name="gate"/> completes.</summary>
    private static async IAsyncEnumerable<ChatDelta> ToolCallThenBlockAsync(ToolCall toolCall, Task gate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatDelta { ToolCalls = [toolCall] };
        await gate.WaitAsync(cancellationToken);
    }

    [Fact]
    public async Task TurnLoop_ToolBatch_PreparesApprovalsAndExecutesExactlyOnce()
    {
        // Arrange - PrepareToolsForApprovals cancels every pending gate first, so a second call
        // per batch would drop an approval the user already made.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var toolCall = CreateToolCall("call_1", "read_file");
        SetupTurnStreams(
            [new ChatDelta { Content = "turn1", ToolCalls = [toolCall] }],
            [new ChatDelta { Content = "turn2" }]);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert
        _mockToolCallHandler.Verify(x => x.PrepareToolsForApprovals(It.IsAny<List<ToolCall>>()), Times.Once);
        _mockToolCallHandler.Verify(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(),
            It.IsAny<AppMode>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TurnLoop_CancelledMidBatch_EveryToolCallHasResult()
    {
        // Arrange - two calls in the batch; the run is cancelled after the first one finished.
        // An assistant(tool_calls) turn without matching tool messages is a hard API 400 on the
        // next turn, so the remainder must be stubbed.
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);

        var first = CreateToolCall("call_1", "read_file");
        var second = CreateToolCall("call_2", "read_file");
        SetupTurnStreams(
            [new ChatDelta { Content = "turn1", ToolCalls = [first, second] }],
            [new ChatDelta { Content = "turn2" }]);
        SetupProcessStreamConsume();

        AiChat? instance = null;
        _mockToolCallHandler
            .Setup(x => x.ProcessToolCallsAsync(It.IsAny<List<ToolCall>>(), It.IsAny<AppMode>(), It.IsAny<CancellationToken>()))
            .Returns(async (List<ToolCall> calls, AppMode _, CancellationToken ct) =>
            {
                calls[0].Result = new ToolResult { Name = calls[0].Function.Name, Content = "done", Success = true };
                await CancelRunAsync(instance!);
                throw new OperationCanceledException(ct);
            });

        var cut = Render<AiChat>();
        instance = cut.Instance;

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert
        var turn = _session.GetMessagesSnapshot()[1];
        Assert.NotNull(turn.ToolCalls);
        Assert.Equal(2, turn.ToolCalls.Count);
        Assert.All(turn.ToolCalls, call => Assert.NotNull(call.Result));
        Assert.True(turn.ToolCalls[0].Result!.Success);
        Assert.False(turn.ToolCalls[1].Result!.Success);
        Assert.All(_session.GetMessagesSnapshot(), m => Assert.False(m.IsStreaming));
    }


    // ═══════════════════════════════════════════════════════════════════════
    //  Persistence invariant: the terminal state of a run always reaches storage,
    //  and a run pins its session (autosave persists by instance).
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunFailure_PersistsTerminalStateOnExit()
    {
        // Arrange - the run fails with an exhausted retry budget. The error text and every
        // completed turn must reach storage: before the fix nothing was saved after the run
        // started, so a reload after an error kept an empty assistant bubble and lost the run.
        _commonOptions.MaxRetries = 1;
        SetupFailingStream(postStreamContentSnapshots: null, failureMessage: "boom");

        var savedContents = new List<string>();
        _mockChatService
            .Setup(x => x.SaveSessionAsync())
            .Callback(() =>
            {
                var messages = _session.GetMessagesSnapshot();
                savedContents.Add(messages.Count == 0 ? string.Empty : messages[^1].Content);
            })
            .Returns(Task.CompletedTask);

        var cut = Render<AiChat>();

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("hi"));

        // Assert - at least one write carried the final error message.
        Assert.Contains(savedContents, content => content.Contains("Error:") && content.Contains("boom"));
    }

    [Fact]
    public async Task NewSession_WhileRunInFlight_IsIgnored()
    {
        // Arrange - an agent run is pinned to the session it started in; swapping mid-run
        // would scatter turns of one run across two sessions.
        var release = new TaskCompletionSource();
        _mockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession _, VisualChatMessage? _, IReadOnlyList<Tool>? _, CompletionsResult capture, CancellationToken ct)
                => StreamUntilReleasedLocal(release, capture, ct));
        _mockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        _mockChatService.Setup(x => x.NewSessionAsync()).Returns(Task.CompletedTask);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();
        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("start"));
        await WaitUntilAsync(() => _session.GetMessagesSnapshot()[^1].Content == "streaming...");

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.NewSessionAsync());

        // Assert
        _mockChatService.Verify(x => x.NewSessionAsync(), Times.Never);

        release.TrySetResult();
        await run;
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
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (VisualChatMessage message,
                IAsyncEnumerable<ChatDelta> deltas,
                Action<string>? onContentUpdate,
                Action<List<ToolCall>>? onToolCallsUpdate,
                Action? onStateChange,
                CompletionsResult resultCapture,
                CancellationToken cancellationToken) =>
            {
                message.IsStreaming = true;
                try
                {
                    await foreach (var delta in deltas.WithCancellation(cancellationToken))
                    {
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
                    // Real ProcessStreamAsync parity: the message is ALWAYS closed, also when
                    // the stream throws.
                    message.IsStreaming = false;
                    // AFTER the pump returns: HandleErrorAsync (called by the component's
                    // catch) may have overwritten the content — the snapshot includes it.
                    postStreamContentSnapshots?.Add(message.Content);
                }
            });

        SetupTurnScripts((_, ct) => CreateFailingStreamAsync(
            [new ChatDelta { Content = "Partial answer before failure" }], failureMessage, ct));
    }

    /// <summary>
    /// Fails the turn with the exception the real transport produces (see
    /// <see cref="StreamThenThrowAsync"/> for why the type is part of the contract).
    /// </summary>
    private static async IAsyncEnumerable<ChatDelta> CreateFailingStreamAsync(
        ChatDelta[] deltas, string failureMessage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var delta in deltas)
        {
            yield return delta;
        }

        throw new LlmApiException(failureMessage);
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
