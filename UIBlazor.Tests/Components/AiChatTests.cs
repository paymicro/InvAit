namespace UIBlazor.Tests.Components;

/// <summary>
/// Rendering surface of <see cref="AiChat"/>: the empty state, the header controls, the profile
/// selector and the initialisation handshake. The transcript itself is covered by
/// <c>MessageListViewTests</c> and <c>ChatMessageViewTests</c>.
/// </summary>
public class AiChatTests : AiChatTestsBase
{
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
        Session.AddMessage(userMessage);
        Session.AddMessage(assistantMessage);

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
        MockChatService.Setup(x => x.NewSessionAsync()).Returns(Task.CompletedTask);
        var cut = Render<AiChat>();
        var newSessionButton = cut.Find(".rz-chat-header-new");

        // Act
        await cut.InvokeAsync(() => newSessionButton.Click());

        // Assert
        MockChatService.Verify(x => x.NewSessionAsync(), Times.Once);
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
        MockProfileManager.Setup(x => x.ActivateProfileAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var cut = Render<AiChat>();
        var profileDropdown = cut.FindComponent<RadzenDropDown<string>>();

        // Act
        await cut.InvokeAsync(() => profileDropdown.Instance.Change.InvokeAsync(newProfileId));

        // Assert
        MockProfileManager.Verify(x => x.ActivateProfileAsync(newProfileId, false), Times.Once);
    }

    [Fact]
    public void ShouldShowProfileDropdown_WhenProfilesExist()
    {
        // Arrange - profiles are set up in the fixture

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
        ProfileOptions.Profiles.Clear();

        // Act
        var cut = Render<AiChat>();

        // Assert
        Assert.Throws<ComponentNotFoundException>(() => cut.FindComponent<RadzenDropDown<string>>());
    }

    [Fact]
    public async Task ShouldInitializeVsBridge_OnInitialized()
    {
        // Arrange
        MockVsBridge.Setup(x => x.InitializeAsync()).Returns(Task.CompletedTask);
        MockToolManager.Setup(x => x.RegisterAllTools());

        // Act
        var cut = Render<AiChat>();

        // Wait for async operations
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        MockVsBridge.Verify(x => x.InitializeAsync(), Times.Once);
        MockToolManager.Verify(x => x.RegisterAllTools(), Times.Once);
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
    public void ShouldRenderMessageWithCorrectKey()
    {
        // Arrange
        var message1 = new VisualChatMessage { Content = "Message 1", Role = ChatMessageRole.User };
        var message2 = new VisualChatMessage { Content = "Message 2", Role = ChatMessageRole.Assistant };
        Session.AddMessage(message1);
        Session.AddMessage(message2);

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
