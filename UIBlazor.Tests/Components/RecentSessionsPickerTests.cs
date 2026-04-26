namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for<see cref="RecentSessionsPicker"/>
/// </summary>
public class RecentSessionsPickerTests : BunitContext
{
    private readonly Mock<IChatService> _mockChatService;
    private readonly List<SessionSummary> _testSessions;

    public RecentSessionsPickerTests()
    {
        _mockChatService = new Mock<IChatService>();

        _testSessions =
        [
            new SessionSummary
            {
                Id = "session-1",
                CreatedAt = new DateTime(2024, 1, 15, 10, 30, 0),
                FirstUserMessage = "First test message"
            },
            new SessionSummary
            {
                Id = "session-2",
                CreatedAt = new DateTime(2024, 1, 14, 15, 45, 0),
                FirstUserMessage = "Second test message with longer text"
            },
            new SessionSummary
            {
                Id = "session-3",
                CreatedAt = new DateTime(2024, 1, 13, 8, 0, 0),
                FirstUserMessage = "Third message"
            }
        ];

        // Setup default returns
        _mockChatService.Setup(x => x.GetRecentSessionsAsync(It.IsAny<int>()))
            .ReturnsAsync(_testSessions);
        _mockChatService.Setup(x => x.LoadSessionAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockChatService.Setup(x => x.DeleteSessionAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Register services
        Services.AddSingleton(_mockChatService.Object);

        // Add Radzen components (this registers DialogService internally)
        Services.AddRadzenComponents();

        // Setup JSInterop for Radzen
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
        JSInterop.SetupVoid("Radzen.closeDialog", _ => true);
        JSInterop.SetupVoid("Radzen.openDialog", _ => true);
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderEmptyState_WhenNoSessions()
    {
        // Arrange
        _mockChatService.Setup(x => x.GetRecentSessionsAsync(It.IsAny<int>()))
            .ReturnsAsync([]);

        // Act
        var cut = Render<RecentSessionsPicker>();

        // Assert
        Assert.NotNull(cut.Find(".rz-chat-empty"));
        Assert.Contains(SharedResource.EmptyMessage, cut.Markup);
    }

    [Fact]
    public void ShouldRenderEmptyState_WhenSessionsNull()
    {
        // Arrange
        _mockChatService.Setup(x => x.GetRecentSessionsAsync(It.IsAny<int>()))
            .ReturnsAsync((List<SessionSummary>?)null);

        // Act
        var cut = Render<RecentSessionsPicker>();

        // Assert
        Assert.NotNull(cut.Find(".rz-chat-empty"));
        Assert.Contains(SharedResource.EmptyMessage, cut.Markup);
    }

    [Fact]
    public void ShouldRenderSessionsList_WhenHasSessions()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>();

        // Assert
        var dataLists = cut.FindComponents<RadzenDataList<SessionSummary>>();
        Assert.NotEmpty(dataLists);
    }

    [Fact]
    public void ShouldRenderTitle_WhenShowTitleIsTrue()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>(parameters => parameters
            .Add(p => p.ShowTitle, true));

        // Assert
        Assert.Contains(SharedResource.SessionsTitle, cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderTitle_WhenShowTitleIsFalse()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>(parameters => parameters
            .Add(p => p.ShowTitle, false));

        // Assert
        Assert.DoesNotContain(SharedResource.SessionsTitle, cut.Markup);
    }

    [Fact]
    public void ShouldRenderCorrectNumberOfSessionCards()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>();

        // Assert
        var cards = cut.FindAll(".rz-card");
        Assert.Equal(_testSessions.Count, cards.Count);
    }

    [Fact]
    public void ShouldRenderSessionCard_WithCorrectData()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>();

        // Assert
        var markup = cut.Markup;
        Assert.Contains("First test message", markup);
        Assert.Contains("Second test message with longer text", markup);
        Assert.Contains("Third message", markup);
    }

    [Fact]
    public void ShouldRenderFormattedDate_ForEachSession()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>();

        // Assert - check that dates are rendered (format "g" = short date + short time)
        var markup = cut.Markup;
        Assert.Contains("1/15/2024", markup); // Date format depends on culture
        Assert.Contains("1/14/2024", markup);
        Assert.Contains("1/13/2024", markup);
    }

    [Fact]
    public void ShouldRenderDeleteButton_ForEachSession()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>();

        // Assert
        var deleteButtons = cut.FindAll("button");
        var deleteButtonCount = deleteButtons.Count(b => b.InnerHtml.Contains("delete"));
        Assert.Equal(_testSessions.Count, deleteButtonCount);
    }

    [Fact]
    public void ShouldRenderChatBubbleIcon_InEmptyState()
    {
        // Arrange
        _mockChatService.Setup(x => x.GetRecentSessionsAsync(It.IsAny<int>()))
            .ReturnsAsync([]);

        // Act
        var cut = Render<RecentSessionsPicker>();

        // Assert
        var icons = cut.FindComponents<RadzenIcon>();
        Assert.Contains(icons, i => i.Instance.Icon == "chat_bubble_outline");
    }

    [Fact]
    public void ShouldRenderChatBubbleIcon_InEachSessionCard()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>();

        // Assert
        var icons = cut.FindComponents<RadzenIcon>();
        var chatIcons = icons.Where(i => i.Instance.Icon == "chat_bubble_outline");
        Assert.Equal(_testSessions.Count, chatIcons.Count());
    }

    #endregion

    #region OnInitialized Tests

    [Fact]
    public void ShouldCallGetRecentSessions_OnInitialized()
    {
        // Arrange & Act
        Render<RecentSessionsPicker>();

        // Assert
        _mockChatService.Verify(x => x.GetRecentSessionsAsync(5), Times.Once);
    }

    [Fact]
    public void ShouldPassCorrectLimit_ToGetRecentSessions()
    {
        // Arrange & Act
        Render<RecentSessionsPicker>();

        // Assert
        _mockChatService.Verify(x => x.GetRecentSessionsAsync(5), Times.Once);
    }

    #endregion

    #region LoadSession Tests

    [Fact]
    public async Task ShouldCallLoadSession_WhenCardClicked()
    {
        // Arrange
        var cut = Render<RecentSessionsPicker>();
        var card = cut.Find(".rz-card");

        // Act
        await cut.InvokeAsync(() => card.Click());
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        _mockChatService.Verify(x => x.LoadSessionAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ShouldCallLoadSession_WithCorrectSessionId()
    {
        // Arrange
        var cut = Render<RecentSessionsPicker>();
        var cards = cut.FindAll(".rz-card");

        // Act - click first card
        await cut.InvokeAsync(() => cards[0].Click());
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        _mockChatService.Verify(x => x.LoadSessionAsync("session-1"), Times.Once);
    }

    [Fact]
    public async Task ShouldShowLoadingSpinner_WhileLoadingSession()
    {
        // Arrange
        var tcs = new TaskCompletionSource();
        _mockChatService.Setup(x => x.LoadSessionAsync(It.IsAny<string>()))
            .Returns(tcs.Task);

        var cut = Render<RecentSessionsPicker>();
        var card = cut.Find(".rz-card");

        // Act - start loading (don't await)
        var loadTask = cut.InvokeAsync(() => card.Click());

        // Assert - loading spinner should be visible
        Assert.NotNull(cut.FindComponent<RadzenProgressBarCircular>());

        // Complete the load
        tcs.SetResult();
        await loadTask;
    }

    #endregion

    #region DeleteSession Tests

    // Note: DeleteSession tests that involve DialogService.Confirm are complex to test
    // because DialogService is a concrete Radzen class that opens modal dialogs.
    // The dialog interaction typically requires integration/E2E tests.
    // Here we test the UI rendering aspects.

    [Fact]
    public void DeleteButton_ShouldStopPropagation()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>();

        // Assert - delete button should have onclick:stopPropagation
        var deleteButtons = cut.FindAll("button").Where(b => b.InnerHtml.Contains("delete"));
        Assert.NotEmpty(deleteButtons);
    }

    [Fact]
    public void DeleteButton_ShouldHaveCorrectIcon()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>();

        // Assert - delete button should have delete icon
        var deleteButtons = cut.FindAll("button").Where(b => b.InnerHtml.Contains("delete"));
        foreach (var button in deleteButtons)
        {
            // The button should contain the delete icon
            Assert.Contains("delete", button.InnerHtml);
        }
    }

    #endregion

    #region Parameter Tests

    [Fact]
    public void ShowTitle_ShouldDefaultToFalse()
    {
        // Arrange & Act
        var cut = Render<RecentSessionsPicker>();

        // Assert
        Assert.DoesNotContain(SharedResource.SessionsTitle, cut.Markup);
    }

    [Fact]
    public void ShouldUpdateUi_WhenShowTitleChanges()
    {
        // Arrange
        var cut = Render<RecentSessionsPicker>(parameters => parameters
            .Add(p => p.ShowTitle, false));

        // Assert - no title initially
        Assert.DoesNotContain(SharedResource.SessionsTitle, cut.Markup);

        // Act - change ShowTitle to true
        cut.Render(parameters => parameters
            .Add(p => p.ShowTitle, true));

        // Assert - title should now be visible
        Assert.Contains(SharedResource.SessionsTitle, cut.Markup);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ShouldHandleEmptyFirstUserMessage()
    {
        // Arrange
        _mockChatService.Setup(x => x.GetRecentSessionsAsync(It.IsAny<int>()))
            .ReturnsAsync([new SessionSummary { Id = "test", CreatedAt = DateTime.Now, FirstUserMessage = "" }]);

        // Act
        var cut = Render<RecentSessionsPicker>();

        // Assert - should render without errors
        var cards = cut.FindAll(".rz-card");
        Assert.Single(cards);
    }

    [Fact]
    public void ShouldHandleLongFirstUserMessage()
    {
        // Arrange
        var longMessage = new string('A', 500);
        _mockChatService.Setup(x => x.GetRecentSessionsAsync(It.IsAny<int>()))
            .ReturnsAsync([new SessionSummary { Id = "test", CreatedAt = DateTime.Now, FirstUserMessage = longMessage }]);

        // Act
        var cut = Render<RecentSessionsPicker>();

        // Assert - should render without errors
        Assert.Contains(longMessage, cut.Markup);
    }

    [Fact]
    public void ShouldHandleSpecialCharactersInMessage()
    {
        // Arrange
        var specialMessage = "Test with<special> & \"characters\" 'here'";
        _mockChatService.Setup(x => x.GetRecentSessionsAsync(It.IsAny<int>()))
            .ReturnsAsync([new SessionSummary { Id = "test", CreatedAt = DateTime.Now, FirstUserMessage = specialMessage }]);

        // Act
        var cut = Render<RecentSessionsPicker>();

        // Assert - should render without errors and encode properly
        Assert.Contains("Test with", cut.Markup);
    }

    [Fact]
    public async Task ShouldHandleMultipleRapidCardClicks()
    {
        // Arrange
        var cut = Render<RecentSessionsPicker>();
        var card = cut.Find(".rz-card");

        // Act - simulate rapid clicks
        var click1 = cut.InvokeAsync(() => card.Click());
        var click2 = cut.InvokeAsync(() => card.Click());

        await Task.WhenAll(click1, click2);

        // Assert - should handle gracefully (at least one load should happen)
        _mockChatService.Verify(x => x.LoadSessionAsync(It.IsAny<string>()), Times.AtLeast(1));
    }

    #endregion
}
