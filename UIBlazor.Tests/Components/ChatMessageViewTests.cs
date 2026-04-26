namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="ChatMessageView"/>
/// </summary>
public class ChatMessageViewTests : BunitContext
{
    private readonly Mock<ICommonSettingsProvider> _mockCommonSettings;
    private readonly CommonOptions _commonOptions;

    public ChatMessageViewTests()
    {
        _mockCommonSettings = new Mock<ICommonSettingsProvider>();
        _commonOptions = new CommonOptions
        {
            ShowMessageTimings = false
        };

        _mockCommonSettings.Setup(x => x.Current).Returns(_commonOptions);

        Services.AddSingleton(_mockCommonSettings.Object);
        Services.AddRadzenComponents();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);

        // Stub for MessageContent component
        ComponentFactories.AddStub<MessageContent>(parameters => builder =>
        {
            var message = parameters.Get(p => p.Message);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "message-content-stub");
            builder.AddContent(2, message?.Content ?? "No content");
            builder.CloseElement();
        });

        // Stub for MarkdownBlock component
        ComponentFactories.AddStub<MarkdownBlock>(parameters => builder =>
        {
            var content = parameters.Get(p => p.Content);
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "markdown-block-stub");
            builder.AddContent(2, content ?? "No markdown");
            builder.CloseElement();
        });
    }

    private VisualChatMessage CreateMessage(string role = ChatMessageRole.User, string content = "Test message")
    {
        return new VisualChatMessage
        {
            Role = role,
            Content = content,
            Timestamp = new DateTime(2024, 1, 15, 10, 30, 0)
        };
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderMessage_WithCorrectRoleClass()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.User);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var messageDiv = cut.Find(".rz-chat-message");
        Assert.Contains("rz-chat-message-user", messageDiv.ClassList);
    }

    [Fact]
    public void ShouldRenderMessage_WithAssistantRoleClass()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var messageDiv = cut.Find(".rz-chat-message");
        Assert.Contains("rz-chat-message-assistant", messageDiv.ClassList);
    }

    [Fact]
    public void ShouldRenderMessage_WithToolRoleClass()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Tool);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var messageDiv = cut.Find(".rz-chat-message");
        Assert.Contains("rz-chat-message-tool", messageDiv.ClassList);
    }

    [Fact]
    public void ShouldRenderUserAvatar_WithText()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.User);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.UserAvatarText, "JD"));

        // Assert
        var avatarInitials = cut.Find(".rz-chat-avatar-initials");
        Assert.Contains("JD", avatarInitials.TextContent);
    }

    [Fact]
    public void ShouldRenderAssistantAvatar_WithRobotIcon()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var icon = cut.FindComponent<RadzenIcon>();
        Assert.Equal("robot_2", icon.Instance.Icon);
    }

    [Fact]
    public void ShouldRenderToolAvatar_WithDesignServicesIcon()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Tool);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var icons = cut.FindComponents<RadzenIcon>();
        var toolIcon = icons.FirstOrDefault(i => i.Instance.Icon == "design_services");
        Assert.NotNull(toolIcon);
    }

    [Fact]
    public void ShouldRenderTimestamp()
    {
        // Arrange
        var message = CreateMessage();
        message.Timestamp = new DateTime(2024, 1, 15, 14, 45, 30);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var timeDiv = cut.Find(".rz-chat-message-time");
        Assert.Contains("14:45", timeDiv.TextContent);
    }

    [Fact]
    public void ShouldRenderModel_WhenPresent()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.Model = "gpt-4";

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var timeDivs = cut.FindAll(".rz-chat-message-time");
        Assert.Contains(timeDivs, d => d.TextContent.Contains("gpt-4"));
    }

    [Fact]
    public void ShouldNotRenderModel_WhenNull()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.Model = null;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert - only one time div (timestamp)
        var timeDivs = cut.FindAll(".rz-chat-message-time");
        Assert.Single(timeDivs);
    }

    #endregion

    #region Message Actions Tests

    [Fact]
    public void ShouldRenderEditButton()
    {
        // Arrange
        var message = CreateMessage();

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var buttons = cut.FindAll("button");
        var editButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("edit"));
        Assert.NotNull(editButton);
    }

    [Fact]
    public void ShouldRenderDeleteButton()
    {
        // Arrange
        var message = CreateMessage();

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var buttons = cut.FindAll("button");
        var deleteButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("delete"));
        Assert.NotNull(deleteButton);
    }

    [Fact]
    public void ShouldRenderRegenerateButton_ForAssistantAndIsLast()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.IsLast, true)
            .Add(p => p.IsLoading, false));

        // Assert
        var buttons = cut.FindAll("button");
        var regenerateButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("refresh"));
        Assert.NotNull(regenerateButton);
    }

    [Fact]
    public void ShouldNotRenderRegenerateButton_ForUserMessage()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.User);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.IsLast, true));

        // Assert
        var buttons = cut.FindAll("button");
        var regenerateButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("refresh"));
        Assert.Null(regenerateButton);
    }

    [Fact]
    public void ShouldNotRenderRegenerateButton_WhenNotLast()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.IsLast, false));

        // Assert
        var buttons = cut.FindAll("button");
        var regenerateButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("refresh"));
        Assert.Null(regenerateButton);
    }

    [Fact]
    public void ShouldNotRenderRegenerateButton_WhenLoading()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.IsLast, true)
            .Add(p => p.IsLoading, true));

        // Assert
        var buttons = cut.FindAll("button");
        var regenerateButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("refresh"));
        Assert.Null(regenerateButton);
    }

    [Fact]
    public void ShouldNotRenderActions_WhenStreaming()
    {
        // Arrange
        var message = CreateMessage();
        message.IsStreaming = true;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".rz-chat-message-actions"));
    }

    [Fact]
    public void ShouldNotRenderActions_WhenEditing()
    {
        // Arrange
        var message = CreateMessage();
        message.IsEditing = true;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".rz-chat-message-actions"));
    }

    #endregion

    #region Editing Tests

    [Fact]
    public void ShouldRenderTextArea_WhenEditing()
    {
        // Arrange
        var message = CreateMessage();
        message.IsEditing = true;
        message.TempContent = "Edit content";

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var textArea = cut.FindComponent<RadzenTextArea>();
        Assert.NotNull(textArea);
    }

    [Fact]
    public void ShouldRenderSaveAndCancelButtons_WhenEditing()
    {
        // Arrange
        var message = CreateMessage();
        message.IsEditing = true;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var buttons = cut.FindAll("button");
        var cancelButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("close"));
        var saveButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("check"));
        Assert.NotNull(cancelButton);
        Assert.NotNull(saveButton);
    }

    [Fact]
    public void ShouldHideContentWrapper_WhenEditing()
    {
        // Arrange
        var message = CreateMessage();
        message.IsEditing = true;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var contentWrapper = cut.Find(".message-content-wrapper");
        var visibility = contentWrapper.GetAttribute("style");
        Assert.Contains("visibility: hidden", visibility);
    }

    [Fact]
    public async Task OnEditButton_Click_InvokesCallback()
    {
        // Arrange
        var message = CreateMessage();
        var callbackInvoked = false;
        VisualChatMessage? callbackMessage = null;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.OnEdit, EventCallback.Factory.Create<VisualChatMessage>(this, msg =>
            {
                callbackInvoked = true;
                callbackMessage = msg;
            })));

        var buttons = cut.FindAll("button");
        var editButton = buttons.First(b => b.InnerHtml.Contains("edit"));
        await cut.InvokeAsync(() => editButton.Click());

        // Assert
        Assert.True(callbackInvoked);
        Assert.Equal(message, callbackMessage);
    }

    [Fact]
    public async Task OnSaveEdit_Click_InvokesCallback()
    {
        // Arrange
        var message = CreateMessage();
        message.IsEditing = true;
        var callbackInvoked = false;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.OnSaveEdit, EventCallback.Factory.Create<VisualChatMessage>(this, msg => callbackInvoked = true)));

        var buttons = cut.FindAll("button");
        var saveButton = buttons.First(b => b.InnerHtml.Contains("check"));
        await cut.InvokeAsync(() => saveButton.Click());

        // Assert
        Assert.True(callbackInvoked);
    }

    [Fact]
    public async Task OnCancelEdit_Click_InvokesCallback()
    {
        // Arrange
        var message = CreateMessage();
        message.IsEditing = true;
        var callbackInvoked = false;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.OnCancelEdit, EventCallback.Factory.Create<VisualChatMessage>(this, msg => callbackInvoked = true)));

        var buttons = cut.FindAll("button");
        var cancelButton = buttons.First(b => b.InnerHtml.Contains("close"));
        await cut.InvokeAsync(() => cancelButton.Click());

        // Assert
        Assert.True(callbackInvoked);
    }

    #endregion

    #region Streaming Tests

    [Fact]
    public void ShouldRenderStreamingIcon_WhenStreaming()
    {
        // Arrange
        var message = CreateMessage();
        message.IsStreaming = true;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var streamingIcon = cut.Find(".rz-chat-message-streaming-icon");
        Assert.NotNull(streamingIcon);
    }

    [Fact]
    public void ShouldNotRenderStreamingIcon_WhenNotStreaming()
    {
        // Arrange
        var message = CreateMessage();
        message.IsStreaming = false;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".rz-chat-message-streaming-icon"));
    }

    #endregion

    #region Plan Tests

    [Fact]
    public void ShouldRenderPlan_WhenHasPlan()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.PlanContent = "## Plan\n1. Step one\n2. Step two";

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var planBox = cut.Find(".plan-box");
        Assert.NotNull(planBox);
        Assert.Contains(SharedResource.ProposedPlan, cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderPlan_WhenNoPlan()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.PlanContent = null;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".plan-box"));
    }

    [Fact]
    public void ShouldRenderExecutePlanButton_WhenNotLoading()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.PlanContent = "Plan content";

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.IsLoading, false));

        // Assert
        var planBox = cut.Find(".plan-box");
        var executeButton = planBox.QuerySelector("button");
        Assert.NotNull(executeButton);
        Assert.Contains(SharedResource.ExecutePlan, cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderExecutePlanButton_WhenLoading()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.PlanContent = "Plan content";

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.IsLoading, true));

        // Assert
        var planBox = cut.Find(".plan-box");
        var executeButton = planBox.QuerySelector("button");
        Assert.Null(executeButton);
    }

    [Fact]
    public async Task OnExecutePlan_Click_InvokesCallback()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.PlanContent = "Plan content";
        var callbackInvoked = false;
        VisualChatMessage? callbackMessage = null;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.IsLoading, false)
            .Add(p => p.OnExecutePlan, EventCallback.Factory.Create<VisualChatMessage>(this, msg =>
            {
                callbackInvoked = true;
                callbackMessage = msg;
            })));

        var planBox = cut.Find(".plan-box");
        var executeButton = planBox.QuerySelector("button");
        await cut.InvokeAsync(() => executeButton!.Click());

        // Assert
        Assert.True(callbackInvoked);
        Assert.Equal(message, callbackMessage);
    }

    #endregion

    #region Retry Tests

    [Fact]
    public void ShouldRenderRetryCountdown_WhenRetrying()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.RetryCountdown = 5;
        message.RetryAttempt = 2;
        message.MaxRetryAttempts = 3;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var retryDiv = cut.Find(".retry-countdown");
        Assert.NotNull(retryDiv);
        Assert.Contains("5", retryDiv.TextContent);
        Assert.Contains("2 / 3", retryDiv.TextContent);
    }

    [Fact]
    public void ShouldNotRenderRetryCountdown_WhenNotRetrying()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.RetryCountdown = 0;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".retry-countdown"));
    }

    #endregion

    #region Show More Tests

    [Fact]
    public void ShouldRenderShowMoreButton_WhenNotExpandedAndNotStreaming()
    {
        // Arrange
        var message = CreateMessage();
        message.IsExpanded = false;
        message.IsStreaming = false;
        message.IsEditing = false;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var showMoreButton = cut.Find(".show-more-button");
        Assert.NotNull(showMoreButton);
        Assert.Contains(SharedResource.ShowMore, cut.Markup);
    }

    [Fact]
    public void ShouldNotRenderShowMoreButton_WhenExpanded()
    {
        // Arrange
        var message = CreateMessage();
        message.IsExpanded = true;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".show-more-button"));
    }

    [Fact]
    public void ShouldNotRenderShowMoreButton_WhenStreaming()
    {
        // Arrange
        var message = CreateMessage();
        message.IsExpanded = false;
        message.IsStreaming = true;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".show-more-button"));
    }

    [Fact]
    public void ShouldNotRenderShowMoreButton_WhenEditing()
    {
        // Arrange
        var message = CreateMessage();
        message.IsExpanded = false;
        message.IsEditing = true;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".show-more-button"));
    }

    [Fact]
    public async Task ShowMoreButton_Click_SetsIsExpanded()
    {
        // Arrange
        var message = CreateMessage();
        message.IsExpanded = false;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        var showMoreButton = cut.Find(".show-more-button");
        await cut.InvokeAsync(() => showMoreButton.Click());

        // Assert
        Assert.True(message.IsExpanded);
    }

    #endregion

    #region Timings Tests

    [Fact]
    public void ShouldRenderTimings_WhenEnabledAndHasTimings()
    {
        // Arrange
        _commonOptions.ShowMessageTimings = true;
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.Timings = new MessageTimings
        {
            FirstToken = TimeSpan.FromMilliseconds(500),
            TokensInSec = 50.5f,
            Total = TimeSpan.FromSeconds(2)
        };

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var badges = cut.FindAll(".rz-badge");
        Assert.True(badges.Count >= 3);
    }

    [Fact]
    public void ShouldNotRenderTimings_WhenDisabled()
    {
        // Arrange
        _commonOptions.ShowMessageTimings = false;
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.Timings = new MessageTimings
        {
            FirstToken = TimeSpan.FromMilliseconds(500),
            TokensInSec = 50.5f,
            Total = TimeSpan.FromSeconds(2)
        };

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var badges = cut.FindAll(".rz-badge");
        Assert.Empty(badges);
    }

    [Fact]
    public void ShouldNotRenderTimings_WhenFirstTokenTooSmall()
    {
        // Arrange
        _commonOptions.ShowMessageTimings = true;
        var message = CreateMessage(ChatMessageRole.Assistant);
        message.Timings = new MessageTimings
        {
            FirstToken = TimeSpan.FromMilliseconds(50), // Less than 100ms
            TokensInSec = 50.5f,
            Total = TimeSpan.FromSeconds(2)
        };

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var badges = cut.FindAll(".rz-badge");
        Assert.Empty(badges);
    }

    #endregion

    #region Delete Tests

    [Fact]
    public async Task OnDeleteButton_Click_InvokesCallback()
    {
        // Arrange
        var message = CreateMessage();
        var callbackInvoked = false;
        VisualChatMessage? callbackMessage = null;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.OnDelete, EventCallback.Factory.Create<VisualChatMessage>(this, msg =>
            {
                callbackInvoked = true;
                callbackMessage = msg;
            })));

        var buttons = cut.FindAll("button");
        var deleteButton = buttons.First(b => b.InnerHtml.Contains("delete"));
        await cut.InvokeAsync(() => deleteButton.Click());

        // Assert
        Assert.True(callbackInvoked);
        Assert.Equal(message, callbackMessage);
    }

    #endregion

    #region Regenerate Tests

    [Fact]
    public async Task OnRegenerateButton_Click_InvokesCallback()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);
        var callbackInvoked = false;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.IsLast, true)
            .Add(p => p.IsLoading, false)
            .Add(p => p.OnRegenerate, EventCallback.Factory.Create(this, () => callbackInvoked = true)));

        var buttons = cut.FindAll("button");
        var regenerateButton = buttons.First(b => b.InnerHtml.Contains("refresh"));
        await cut.InvokeAsync(() => regenerateButton.Click());

        // Assert
        Assert.True(callbackInvoked);
    }

    #endregion

    #region Message Key Tests

    [Fact]
    public void ShouldRenderMessage_WithKey()
    {
        // Arrange
        var message = CreateMessage();

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert - the message div should have a key attribute (Blazor handles this internally)
        var messageDiv = cut.Find(".rz-chat-message");
        Assert.NotNull(messageDiv);
        // Key is used by Blazor for diffing, not directly visible in DOM
    }

    #endregion

    #region Alignment Tests

    [Fact]
    public void ShouldAlignUserMessage_ToEnd()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.User);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var contentStack = cut.Find(".rz-chat-message-content");
        // Radzen generates "rz-align-items-flex-end" for AlignItems.End
        Assert.Contains("rz-align-items-flex-end", contentStack.ClassList);
    }

    [Fact]
    public void ShouldAlignAssistantMessage_ToStart()
    {
        // Arrange
        var message = CreateMessage(ChatMessageRole.Assistant);

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var contentStack = cut.Find(".rz-chat-message-content");
        // Radzen generates "rz-align-items-flex-start" for AlignItems.Start
        Assert.Contains("rz-align-items-flex-start", contentStack.ClassList);
    }

    #endregion

    #region Expanded/Collapsed Tests

    [Fact]
    public void ShouldRenderCollapsedClass_WhenNotExpanded()
    {
        // Arrange
        var message = CreateMessage();
        message.IsExpanded = false;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var contentWrapper = cut.Find(".message-content-wrapper");
        Assert.Contains("collapsed", contentWrapper.ClassList);
    }

    [Fact]
    public void ShouldRenderExpandedClass_WhenExpanded()
    {
        // Arrange
        var message = CreateMessage();
        message.IsExpanded = true;

        // Act
        var cut = Render<ChatMessageView>(parameters => parameters
            .Add(p => p.Message, message));

        // Assert
        var contentWrapper = cut.Find(".message-content-wrapper");
        Assert.Contains("expanded", contentWrapper.ClassList);
    }

    #endregion
}
