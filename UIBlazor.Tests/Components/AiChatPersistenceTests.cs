namespace UIBlazor.Tests.Components;

/// <summary>
/// Run-level lifecycle of <see cref="AiChat"/>: what reaches storage, what a run pins, and the
/// compression notifications. The persistence invariants are described in
/// <c>docs/dev/chat-service.md</c>.
/// </summary>
public class AiChatPersistenceTests : AiChatTestsBase
{
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
        Session.AddMessage(summary);

        // AiChat injects IJSRuntime (mocked in this fixture), so the scroll call is observed on
        // the mock — bUnit's JSInterop is not in that path.
        var scrollCalls = CaptureJsVoidCalls("scrollToMessage");

        var cut = Render<AiChat>();

        // Act 1: compression started mid-run — the chat would otherwise just look frozen.
        await cut.InvokeAsync(() =>
        {
            MockChatService.Raise(x => x.CompressionStarted += null, Session);
            return Task.CompletedTask;
        });

        Assert.Contains(SharedResource.Compressing, cut.Markup);

        // Act 2: compression finished — history rewritten, summary sits in the middle.
        await cut.InvokeAsync(() =>
        {
            MockChatService.Raise(x => x.CompressionCompleted += null,
                new CompressionReport(Session, summary, 4));
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
            MockChatService.Raise(x => x.CompressionCompleted += null,
                new CompressionReport(branchSession, summary, 3));
            return Task.CompletedTask;
        });

        // Assert
        Assert.Empty(notificationService.Messages);
    }

    [Fact]
    public async Task RunFailure_PersistsTerminalStateOnExit()
    {
        // Arrange - the run fails with an exhausted retry budget. The error text and every
        // completed turn must reach storage: before the fix nothing was saved after the run
        // started, so a reload after an error kept an empty assistant bubble and lost the run.
        CommonOptions.MaxRetries = 1;
        SetupFailingStream(postStreamContentSnapshots: null, failureMessage: "boom");

        var savedContents = new List<string>();
        MockChatService
            .Setup(x => x.SaveSessionAsync())
            .Callback(() =>
            {
                var messages = Session.GetMessagesSnapshot();
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
        MockChatService
            .Setup(x => x.GetTurnCompletionsAsync(It.IsAny<ConversationSession>(), It.IsAny<VisualChatMessage?>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationSession _, VisualChatMessage? _, IReadOnlyList<Tool>? _, CompletionsResult capture, CancellationToken ct)
                => StreamUntilReleasedLocal(release, capture, ct));
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        MockChatService.Setup(x => x.NewSessionAsync()).Returns(Task.CompletedTask);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();
        var run = cut.InvokeAsync(async () => await cut.Instance.SendMessageAsync("start"));
        await WaitUntilAsync(() => Session.GetMessagesSnapshot()[^1].Content == "streaming...");

        // Act
        await cut.InvokeAsync(async () => await cut.Instance.NewSessionAsync());

        // Assert
        MockChatService.Verify(x => x.NewSessionAsync(), Times.Never);

        release.TrySetResult();
        await run;
    }

    /// <summary>
    /// Regression: regenerate removes the last assistant message and adds the new streaming one in
    /// the SAME handler, so one batched render sees "N messages" before and after. While the
    /// transcript cache was keyed by MESSAGE COUNT, that render was answered with the PRE-change
    /// list: the deleted answer stayed on screen and the streaming bubble never entered the DOM
    /// until the count happened to move (the "+1 message" the user reported).
    /// <para>
    /// The click is dispatched through the real event path on purpose: <c>cut.InvokeAsync</c> renders
    /// every <c>StateHasChanged</c> immediately and therefore hid the defect.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Regenerate_ReplacesBubbleImmediately()
    {
        // Arrange: a finished exchange (user + assistant).
        Session.AddMessage(VisualChatMessage.CreateUser("question"));
        var oldAnswer = VisualChatMessage.CreateStreaming();
        oldAnswer.Content = "old answer";
        oldAnswer.IsStreaming = false;
        Session.AddMessage(oldAnswer);

        var release = new TaskCompletionSource();
        SetupTurnScripts((capture, ct) => StreamUntilReleasedLocal(release, capture, ct));
        MockChatService.Setup(x => x.SaveSessionAsync()).Returns(Task.CompletedTask);
        SetupProcessStreamConsume();

        var cut = Render<AiChat>();
        Assert.Equal(2, cut.FindAll(".chat-message-view-stub").Count);

        // Act: click regenerate exactly like a user does (real event dispatch: Blazor batches every
        // StateHasChanged of the handler into ONE render, so the removal and the insertion of the
        // new turn message are observed together).
        _ = cut.Find(".chat-message-view-stub-regenerate").ClickAsync();
        await WaitUntilAsync(() => Session.GetMessagesSnapshot().Any(m => m.Content == "streaming..."));

        // Assert: the DOM shows the NEW streaming bubble, the old answer is gone.
        // Identity, not text: the stub is not a real ChatMessageView, so it does not re-render
        // on tokens (only the real one subscribes to Message.Changed).
        var ids = cut.FindAll(".chat-message-view-stub")
            .Select(s => s.GetAttribute("data-message-id"))
            .ToList();
        Assert.DoesNotContain(oldAnswer.Id, ids);
        Assert.Contains(Session.GetMessagesSnapshot()[^1].Id, ids);

        release.TrySetResult();
        await WaitUntilAsync(() => !Session.GetMessagesSnapshot().Any(m => m.IsStreaming));
    }
}
