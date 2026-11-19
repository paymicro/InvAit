namespace UIBlazor.Tests.Components;

using UIBlazor.Components.Chat;

/// <summary>
/// Tests for <see cref="CompressHint"/>: the visibility band (100+ messages, no run, no compression),
/// the dismissal contract (persisted as the message count, not a flag) and the callback wiring.
/// </summary>
public class CompressHintTests : BunitContext
{
    private readonly Mock<IChatService> _mockChatService;
    private readonly Mock<ILocalStorageService> _mockLocalStorage;
    private readonly ConversationSession _session;

    /// <summary>Persisted dismissal value behind the localStorage mock (0 = never dismissed).</summary>
    private int _dismissedAtCount;

    public CompressHintTests()
    {
        _mockChatService = new Mock<IChatService>();
        _mockLocalStorage = new Mock<ILocalStorageService>();
        _session = new ConversationSession { Id = "session-id" };

        _mockChatService.Setup(x => x.Session).Returns(_session);

        _mockLocalStorage.Setup(x => x.TryGetItemAsync<int>(CompressHint.DismissedAtCountKey))
            .ReturnsAsync(() => _dismissedAtCount);
        _mockLocalStorage.Setup(x => x.SetItemAsync(CompressHint.DismissedAtCountKey, It.IsAny<int>()))
            .Callback<string, int>((_, value) => _dismissedAtCount = value)
            .Returns(Task.CompletedTask);

        Services.AddSingleton(_mockChatService.Object);
        Services.AddSingleton(_mockLocalStorage.Object);
        Services.AddSingleton(new LoggerMock<CompressHint>());
        Services.AddRadzenComponents();

        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
    }

    /// <summary>Fills the session history with <paramref name="count"/> placeholder messages.</summary>
    private void AddMessages(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _session.AddMessage(new VisualChatMessage { Content = $"message-{i}" });
        }
    }

    /// <summary>Raises the session-changed event the way a real run does (after a turn).</summary>
    private Task RaiseSessionChanged(IRenderedComponent<CompressHint> cut) =>
        cut.InvokeAsync(() => _mockChatService.Raise(
            x => x.SessionChanged += null,
            new PropertyChangedEventArgs(nameof(ConversationSession))));

    #region Visibility

    [Fact]
    public void ShouldNotRender_WhenHistoryIsBelowThreshold()
    {
        // Arrange
        AddMessages(CompressHint.MinMessages - 1);

        // Act
        var cut = Render<CompressHint>();

        // Assert
        Assert.Empty(cut.FindAll(".compress-hint"));
    }

    [Fact]
    public void ShouldRender_WhenHistoryReachesThreshold()
    {
        // Arrange
        AddMessages(CompressHint.MinMessages);

        // Act
        var cut = Render<CompressHint>();

        // Assert
        Assert.NotNull(cut.Find(".compress-hint"));
        Assert.Contains(SharedResource.CompressHintAction, cut.Markup);

        // The cross is icon-only, so it must stay announceable for screen readers
        var dismiss = cut.Find(".compress-hint-dismiss");
        Assert.Equal("Dismiss", dismiss.GetAttribute("aria-label"));
    }

    [Fact]
    public void ShouldRenderTooltip_WithActualMessageCount()
    {
        // Arrange
        AddMessages(CompressHint.MinMessages + 7);

        // Act
        var cut = Render<CompressHint>();

        // Assert - the tooltip is rendered by the Radzen tooltip service on hover, so the component
        // has to be asked through the service-less path: the text lives in the child Tooltip.
        var tooltip = cut.FindComponent<UIBlazor.Components.Tooltip>();
        Assert.Equal(
            string.Format(SharedResource.CompressHintTooltip, CompressHint.MinMessages + 7),
            tooltip.Instance.Text);
    }

    [Fact]
    public void ShouldNotRender_WhileRunIsStreaming()
    {
        // Arrange
        AddMessages(CompressHint.MinMessages);

        // Act
        var cut = Render<CompressHint>(parameters => parameters
            .Add(p => p.IsLoading, true));

        // Assert
        Assert.Empty(cut.FindAll(".compress-hint"));
    }

    [Fact]
    public void ShouldNotRender_WhileCompressionIsInFlight()
    {
        // Arrange
        AddMessages(CompressHint.MinMessages);

        // Act
        var cut = Render<CompressHint>(parameters => parameters
            .Add(p => p.IsCompressing, true));

        // Assert
        Assert.Empty(cut.FindAll(".compress-hint"));
    }

    [Fact]
    public async Task ShouldAppear_WhenHistoryGrowsPastThresholdDuringSession()
    {
        // Arrange - below the threshold on first render
        AddMessages(CompressHint.MinMessages - 1);
        var cut = Render<CompressHint>();
        Assert.Empty(cut.FindAll(".compress-hint"));

        // Act - the run appended one more message and raised the session event
        AddMessages(1);
        await RaiseSessionChanged(cut);

        // Assert
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".compress-hint")));
    }

    #endregion

    #region Callback

    [Fact]
    public void ShouldInvokeCompressCallback_Once_WhenButtonClicked()
    {
        // Arrange
        AddMessages(CompressHint.MinMessages);
        var invocations = 0;
        var cut = Render<CompressHint>(parameters => parameters
            .Add(p => p.OnCompress, EventCallback.Factory.Create(this, () => invocations++)));

        // Act
        cut.Find(".compress-hint").Click();

        // Assert
        Assert.Equal(1, invocations);
    }

    #endregion

    #region Dismissal

    [Fact]
    public void ShouldHideAndPersistCount_WhenDismissed()
    {
        // Arrange
        AddMessages(CompressHint.MinMessages);
        var cut = Render<CompressHint>();

        // Act
        cut.Find(".compress-hint-dismiss").Click();

        // Assert - gone from the toolbar...
        Assert.Empty(cut.FindAll(".compress-hint"));
        // ...and the dismissal is stored as the CURRENT count (the growth band baseline).
        _mockLocalStorage.Verify(
            x => x.SetItemAsync(CompressHint.DismissedAtCountKey, CompressHint.MinMessages),
            Times.Once);
    }

    [Fact]
    public void ShouldStayHidden_WhenDismissalWasRestoredAndHistoryBarelyGrew()
    {
        // Arrange - a previous run dismissed the hint at 120 messages
        _dismissedAtCount = 120;
        AddMessages(120 + CompressHint.RemindAfterMoreMessages - 1);

        // Act
        var cut = Render<CompressHint>();

        // Assert
        Assert.Empty(cut.FindAll(".compress-hint"));
    }

    [Fact]
    public async Task ShouldReappear_WhenDismissedHistoryGrewByRemindStep()
    {
        // Arrange - dismissed at 120, now one message short of the reminder band
        _dismissedAtCount = 120;
        AddMessages(120 + CompressHint.RemindAfterMoreMessages - 1);
        var cut = Render<CompressHint>();
        Assert.Empty(cut.FindAll(".compress-hint"));

        // Act - the history reached the reminder threshold
        AddMessages(1);
        await RaiseSessionChanged(cut);

        // Assert
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".compress-hint")));
    }

    [Fact]
    public void ShouldSurviveStorageFailure_WhenDismissing()
    {
        // Arrange - a hint must never break the input, even if persistence throws
        AddMessages(CompressHint.MinMessages);
        _mockLocalStorage
            .Setup(x => x.SetItemAsync(CompressHint.DismissedAtCountKey, It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("storage unavailable"));

        var cut = Render<CompressHint>();

        // Act
        cut.Find(".compress-hint-dismiss").Click();

        // Assert - hidden for this render session, no exception escaped
        Assert.Empty(cut.FindAll(".compress-hint"));
    }

    [Fact]
    public void ShouldTreatHintAsNotDismissed_WhenStorageReadFails()
    {
        // Arrange
        _mockLocalStorage
            .Setup(x => x.TryGetItemAsync<int>(CompressHint.DismissedAtCountKey))
            .ThrowsAsync(new InvalidOperationException("storage unavailable"));
        AddMessages(CompressHint.MinMessages);

        // Act
        var cut = Render<CompressHint>();

        // Assert
        Assert.NotEmpty(cut.FindAll(".compress-hint"));
    }

    #endregion
}
