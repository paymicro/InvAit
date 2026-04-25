namespace UIBlazor.Tests.Components;

using UIBlazor.Components.Settings;
using UIBlazor.Localization;

///<summary>
///<seealso cref="AiChatInput"/>
///</summary>
public class AiChatInputTests : BunitContext
{
    private readonly Mock<IChatService> _mockChatService;
    private readonly Mock<IVsCodeContextService> _mockVsCodeContextService;
    private readonly Mock<IVsBridge> _mockVsBridge;
    private readonly Mock<ICommonSettingsProvider> _mockCommonSettings;
    private readonly Mock<IJSRuntime> _mockJsRuntime;
    private readonly ConversationSession _session;
    private readonly CommonOptions _commonOptions;
    private readonly VsCodeContext _vsCodeContext;

    public AiChatInputTests()
    {
        _mockChatService = new Mock<IChatService>();
        _mockVsCodeContextService = new Mock<IVsCodeContextService>();
        _mockVsBridge = new Mock<IVsBridge>();
        _mockCommonSettings = new Mock<ICommonSettingsProvider>();
        _mockJsRuntime = new Mock<IJSRuntime>();

        // Setup session
        _session = new ConversationSession
        {
            Id = "test-session-id",
            Mode = AppMode.Chat
        };

        // Setup common options
        _commonOptions = new CommonOptions
        {
            SendCurrentFile = true,
            SendSolutionsStricture = true
        };

        // Setup VS Code context with sample files
        _vsCodeContext = new VsCodeContext
        {
            SolutionFiles =
            [
                "  📄 TestFile.cs",
                "  📄 AnotherFile.razor",
                "  📄 Config.json",
                "  📄 README.md"
            ]
        };

        // Setup mock returns
        _mockChatService.Setup(x => x.Session).Returns(_session);
        _mockCommonSettings.Setup(x => x.Current).Returns(_commonOptions);
        _mockVsCodeContextService.Setup(x => x.CurrentContext).Returns(_vsCodeContext);

        // Register services
        Services.AddSingleton(_mockChatService.Object);
        Services.AddSingleton(_mockVsCodeContextService.Object);
        Services.AddSingleton(_mockVsBridge.Object);
        Services.AddSingleton(_mockCommonSettings.Object);
        Services.AddSingleton(_mockJsRuntime.Object);
        Services.AddSingleton(new Mock<ILogger<AiChatInput>>().Object);

        // Add Radzen components
        Services.AddRadzenComponents();

        // Setup JSInterop for Radzen
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);

        // Register component stubs
        RegisterComponentStubs();
    }

    private void RegisterComponentStubs()
    {
        // Stub for FileChip - includes remove button for testing
        ComponentFactories.AddStub<FileChip>(parameters => builder =>
        {
            var token = parameters.Get(p => p.Token);
            var icon = parameters.Get(p => p.Icon);
            var onRemoveClick = parameters.Get(p => p.OnRemoveClick);

            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "file-chip-stub");
            builder.AddAttribute(2, "data-file-path", token?.FilePath);
            builder.AddAttribute(3, "data-icon", icon);
            builder.AddContent(4, token?.FileName ?? "No file");

            // Add remove button
            builder.OpenElement(5, "button");
            builder.AddAttribute(6, "class", "chip-remove");
            builder.AddAttribute(7, "type", "button");
            if (onRemoveClick.HasDelegate)
            {
                builder.AddAttribute(8, "onclick", EventCallback.Factory.Create(this, () => onRemoveClick.InvokeAsync(token)));
            }
            builder.AddContent(9, "×");
            builder.CloseElement();

            builder.CloseElement();
        });

        // Stub for ModelSelector
        ComponentFactories.AddStub<ModelSelector>(builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "model-selector-stub");
            builder.AddContent(2, "ModelSelector Stub");
            builder.CloseElement();
        });
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderTextarea()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>();

        // Assert
        var textarea = cut.Find("textarea.text-input");
        Assert.NotNull(textarea);
        Assert.Equal("1", textarea.GetAttribute("rows"));
    }

    [Fact]
    public void ShouldRenderCorrectPlaceholder_ForChatMode()
    {
        // Arrange
        _session.Mode = AppMode.Chat;

        // Act
        var cut = Render<AiChatInput>();

        // Assert
        var textarea = cut.Find("textarea.text-input");
        Assert.Equal(SharedResource.InputPlaceholder, textarea.GetAttribute("placeholder"));
    }

    [Fact]
    public void ShouldRenderCorrectPlaceholder_ForPlanMode()
    {
        // Arrange
        _session.Mode = AppMode.Plan;

        // Act
        var cut = Render<AiChatInput>();

        // Assert
        var textarea = cut.Find("textarea.text-input");
        Assert.Equal(SharedResource.InputPlaceholderPlan, textarea.GetAttribute("placeholder"));
    }

    [Fact]
    public void ShouldRenderCorrectPlaceholder_ForAgentMode()
    {
        // Arrange
        _session.Mode = AppMode.Agent;

        // Act
        var cut = Render<AiChatInput>();

        // Assert
        var textarea = cut.Find("textarea.text-input");
        Assert.Equal(SharedResource.InputPlaceholderAgent, textarea.GetAttribute("placeholder"));
    }

    [Fact]
    public void ShouldRenderSendButton_WhenNotLoading()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.IsLoading, false));

        // Assert
        var sendButton = cut.Find(".rz-chat-send-btn");
        Assert.NotNull(sendButton);
    }

    [Fact]
    public void ShouldNotRenderSendButton_WhenLoading()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.IsLoading, true));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".rz-chat-send-btn"));
    }

    [Fact]
    public void ShouldRenderCancelButton_WhenLoading()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.IsLoading, true));

        // Assert
        var cancelButton = cut.Find(".rz-chat-cancel-btn");
        Assert.NotNull(cancelButton);
    }

    [Fact]
    public void ShouldNotRenderCancelButton_WhenNotLoading()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.IsLoading, false));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".rz-chat-cancel-btn"));
    }

    [Fact]
    public void ShouldRenderModeDropdown_WithAllAppModes()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>();

        // Assert
        var modeDropdown = cut.FindComponent<RadzenDropDown<AppMode>>();
        Assert.NotNull(modeDropdown);
        var expectedModes = Enum.GetValues<AppMode>().ToList();
        Assert.Equal(expectedModes, modeDropdown.Instance.Data as IEnumerable<AppMode>);
    }

    [Fact]
    public void ShouldRenderModelSelector()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>();

        // Assert
        Assert.NotNull(cut.Find(".model-selector-stub"));
    }

    [Fact]
    public void ShouldRenderToggleButtons_ForSettings()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>();

        // Assert
        var toggleButtons = cut.FindComponents<RadzenToggleButton>();
        Assert.Equal(2, toggleButtons.Count);
    }

    [Fact]
    public void ShouldDisableTextarea_WhenLoading()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.IsLoading, true));

        // Assert
        var textarea = cut.Find("textarea.text-input");
        // In Blazor, disabled attribute is present (even if empty) when disabled
        var disabledAttr = textarea.GetAttribute("disabled");
        Assert.NotNull(disabledAttr);
    }

    #endregion

    #region Input Behavior Tests

    [Fact]
    public void SendButton_ShouldBeDisabled_WhenInputEmptyAndNoTokens()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.IsLoading, false));

        // Assert - check that the button has aria-disabled or disabled attribute
        var sendButton = cut.Find(".rz-chat-send-btn");
        // Radzen renders disabled as aria-disabled="true" or disabled attribute
        var hasDisabled = sendButton.GetAttribute("disabled") != null ||
                          sendButton.GetAttribute("aria-disabled") == "true";
        Assert.True(hasDisabled);
    }

    [Fact]
    public async Task SendButton_ShouldBeEnabled_WhenInputHasText()
    {
        // Arrange
        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.IsLoading, false));

        var textarea = cut.Find("textarea.text-input");

        // Act - simulate input using Input event (triggers @oninput)
        await cut.InvokeAsync(() => textarea.Input("Hello world"));

        // Assert
        var sendButton = cut.Find(".rz-chat-send-btn");
        var isDisabled = sendButton.GetAttribute("disabled") != null ||
                         sendButton.GetAttribute("aria-disabled") == "true";
        Assert.False(isDisabled);
    }

    [Fact]
    public async Task HandleInput_ShowsAllFiles_OnAtPattern()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Act - simulate input ending with " @" (space + @ triggers the pattern)
        await cut.InvokeAsync(() => textarea.Input("Some text @"));

        // Assert - hints menu should appear
        var hintsMenu = cut.Find(".hints-menu");
        Assert.NotNull(hintsMenu);
        var hintItems = cut.FindAll(".hint-item");
        Assert.True(hintItems.Count > 0);
    }

    [Fact]
    public async Task HandleInput_FiltersFiles_ByQuery()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Act - simulate input with @ and query (space + @ + query)
        await cut.InvokeAsync(() => textarea.Input("Some text @Test"));

        // Assert - hints menu should show filtered files
        var hintItems = cut.FindAll(".hint-item");
        Assert.Single(hintItems);
        Assert.Contains("TestFile.cs", hintItems[0].TextContent);
    }

    [Fact]
    public async Task HandleInput_ClearsFilteredFiles_WhenNoAtPresent()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // First, show hints
        await cut.InvokeAsync(() => textarea.Input("text @"));

        // Act - remove @ pattern
        await cut.InvokeAsync(() => textarea.Input("text without at"));

        // Assert - hints menu should not be visible
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".hints-menu"));
    }

    [Fact]
    public async Task HandleInput_ShowsDefaultFiles_WhenNoContext()
    {
        // Arrange
        _mockVsCodeContextService.Setup(x => x.CurrentContext).Returns((VsCodeContext?)null);
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Act
        await cut.InvokeAsync(() => textarea.Input("text @"));

        // Assert - should show fallback files
        var hintItems = cut.FindAll(".hint-item");
        Assert.True(hintItems.Count > 0);
    }

    #endregion

    #region Keyboard Navigation Tests

    [Fact]
    public async Task ArrowDown_MovesSelectionDown()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Show hints first
        await cut.InvokeAsync(() => textarea.Input("text @"));

        var hintItems = cut.FindAll(".hint-item");
        Assert.True(hintItems.Count >= 2, "Need at least 2 items to test navigation");

        // Initial selection should be at index 0
        var initialSelected = cut.Find(".hint-item.selected");
        Assert.True(initialSelected.ClassList.Contains("selected"), "First item should be selected initially");

        // Act - press ArrowDown
        await cut.InvokeAsync(() => textarea.KeyDown("ArrowDown"));

        // Assert - second item should now be selected
        hintItems = cut.FindAll(".hint-item");
        Assert.True(hintItems[1].ClassList.Contains("selected"), "Second item should be selected after ArrowDown");
    }

    [Fact]
    public async Task ArrowUp_MovesSelectionUp()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Show hints first
        await cut.InvokeAsync(() => textarea.Input("text @"));

        // Move down first
        await cut.InvokeAsync(() => textarea.KeyDown("ArrowDown"));
        var selectedAfterDown = cut.Find(".hint-item.selected");

        // Act - press ArrowUp
        await cut.InvokeAsync(() => textarea.KeyDown("ArrowUp"));

        // Assert - should move back up
        var newSelected = cut.Find(".hint-item.selected");
        Assert.NotNull(newSelected);
    }

    [Fact]
    public async Task ArrowDown_WrapsAround_ToFirstItem()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Show hints
        await cut.InvokeAsync(() => textarea.Input("text @"));
        var hintItems = cut.FindAll(".hint-item");
        var lastIndex = hintItems.Count - 1;

        // Move to last item
        for (int i = 0; i< lastIndex; i++)
        {
            await cut.InvokeAsync(() => textarea.KeyDown("ArrowDown"));
        }

        // Act - press ArrowDown one more time
        await cut.InvokeAsync(() => textarea.KeyDown("ArrowDown"));

        // Assert - should wrap to first item
        var selected = cut.Find(".hint-item.selected");
        var allItems = cut.FindAll(".hint-item");
        var selectedIndex = 0;
        for (int i = 0; i< allItems.Count; i++)
        {
            if (allItems[i] == selected)
            {
                selectedIndex = i;
                break;
            }
        }
        Assert.Equal(0, selectedIndex);
    }

    [Fact]
    public async Task Tab_SelectsCurrentFile_FromHints()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Show hints
        await cut.InvokeAsync(() => textarea.Input("text @"));

        // Act - press Tab
        await cut.InvokeAsync(() => textarea.KeyDown("Tab"));

        // Assert - file chip should be added
        var fileChip = cut.Find(".file-chip-stub");
        Assert.NotNull(fileChip);
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".hints-menu"));
    }

    [Fact]
    public async Task Enter_SelectsCurrentFile_FromHints()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Show hints
        await cut.InvokeAsync(() => textarea.Input("text @"));

        // Act - press Enter
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert - file chip should be added
        var fileChip = cut.Find(".file-chip-stub");
        Assert.NotNull(fileChip);
    }

    [Fact]
    public async Task Escape_ClosesHintsMenu()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Show hints
        await cut.InvokeAsync(() => textarea.Input("text @"));
        Assert.NotNull(cut.Find(".hints-menu"));

        // Act - press Escape
        await cut.InvokeAsync(() => textarea.KeyDown("Escape"));

        // Assert - hints menu should be closed
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".hints-menu"));
    }

    [Fact]
    public async Task Enter_SendsMessage_WhenNoHintsVisible()
    {
        // Arrange
        var sendMessageCalled = false;
        string? sentMessage = null;

        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.SendMessage, EventCallback.Factory.Create<string>(this, msg =>
            {
                sendMessageCalled = true;
                sentMessage = msg;
            })));

        var textarea = cut.Find("textarea.text-input");

        // Add some text
        await cut.InvokeAsync(() => textarea.Input("Hello world"));

        // Act - press Enter
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert
        Assert.True(sendMessageCalled);
        Assert.Contains("Hello world", sentMessage);
    }

    [Fact]
    public async Task Enter_DoesNotSend_WhenHintsVisible()
    {
        // Arrange
        var sendMessageCalled = false;

        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.SendMessage, EventCallback.Factory.Create<string>(this, _ => sendMessageCalled = true)));

        var textarea = cut.Find("textarea.text-input");

        // Show hints
        await cut.InvokeAsync(() => textarea.Input("text @"));

        // Act - press Enter
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert - should select file, not send message
        Assert.False(sendMessageCalled);
    }

    #endregion

    #region Token Management Tests

    [Fact]
    public async Task AddFileToken_AddsTokenAndClearsQuery()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Show hints
        await cut.InvokeAsync(() => textarea.Input("some text @Test"));

        // Act - select a file (press Enter)
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert
        var fileChip = cut.Find(".file-chip-stub");
        Assert.NotNull(fileChip);
        Assert.Contains("TestFile.cs", fileChip.TextContent);
    }

    [Fact]
    public async Task RemoveToken_RemovesTokenFromList()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Add a file token
        await cut.InvokeAsync(() => textarea.Input("text @"));
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Verify chip is present
        Assert.NotNull(cut.Find(".file-chip-stub"));

        // Act - click remove button on chip
        var removeButton = cut.Find(".chip-remove");
        await cut.InvokeAsync(() => removeButton.Click());

        // Assert - chip should be removed
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".file-chip-stub"));
    }

    [Fact]
    public async Task SendButton_Enabled_WhenTokensExist()
    {
        // Arrange
        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Add a file token
        await cut.InvokeAsync(() => textarea.Input("text @"));
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Clear text input
        await cut.InvokeAsync(() => textarea.Input(""));

        // Assert - send button should still be enabled due to token
        var sendButton = cut.Find(".rz-chat-send-btn");
        var isDisabled = sendButton.GetAttribute("disabled") != null ||
                         sendButton.GetAttribute("aria-disabled") == "true";
        Assert.False(isDisabled);
    }

    #endregion

    #region Send Message Tests

    [Fact]
    public async Task OnSendClick_InvokesSendMessage_WithTextOnly()
    {
        // Arrange
        string? sentMessage = null;

        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.SendMessage, EventCallback.Factory.Create<string>(this, msg => sentMessage = msg)));

        var textarea = cut.Find("textarea.text-input");

        // Add text
        await cut.InvokeAsync(() => textarea.Input("Hello AI!"));

        // Act - press Enter
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert
        Assert.NotNull(sentMessage);
        Assert.Contains("Hello AI!", sentMessage);
    }

    [Fact]
    public async Task OnSendClick_ClearsState_AfterSending()
    {
        // Arrange
        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.SendMessage, EventCallback.Factory.Create<string>(this, _ => { })));

        var textarea = cut.Find("textarea.text-input");

        // Add text
        await cut.InvokeAsync(() => textarea.Input("Test message"));

        // Act
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert - input should be cleared (value attribute reflects the bound value)
        var textareaAfter = cut.Find("textarea.text-input");
        Assert.Equal("", textareaAfter.GetAttribute("value") ?? "");
    }

    [Fact]
    public async Task OnSendClick_DoesNotSend_WhenEmptyAndNoTokens()
    {
        // Arrange
        var sendMessageCalled = false;

        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.SendMessage, EventCallback.Factory.Create<string>(this, _ => sendMessageCalled = true)));

        var textarea = cut.Find("textarea.text-input");

        // Act - press Enter without input
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert
        Assert.False(sendMessageCalled);
    }

    [Fact]
    public async Task OnSendClick_LoadsFileContent_ViaVsBridge()
    {
        // Arrange
        string? sentMessage = null;

        _mockVsBridge.Setup(x => x.ExecuteToolAsync(
                BuiltInToolEnum.ReadFiles,
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult { Result = "file content here" });

        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.SendMessage, EventCallback.Factory.Create<string>(this, msg => sentMessage = msg)));

        var textarea = cut.Find("textarea.text-input");

        // Add a file token
        await cut.InvokeAsync(() => textarea.Input("@"));
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Add some text
        await cut.InvokeAsync(() => textarea.Input("Check this file"));

        // Act - send
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert
        _mockVsBridge.Verify(x => x.ExecuteToolAsync(
            BuiltInToolEnum.ReadFiles,
            It.IsAny<Dictionary<string, object>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("file content here", sentMessage);
    }

    [Fact]
    public async Task OnSendClick_CombinesTextAndFileContent()
    {
        // Arrange
        string? sentMessage = null;

        _mockVsBridge.Setup(x => x.ExecuteToolAsync(
                BuiltInToolEnum.ReadFiles,
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult { Result = "FILE_CONTENT" });

        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.SendMessage, EventCallback.Factory.Create<string>(this, msg => sentMessage = msg)));

        var textarea = cut.Find("textarea.text-input");

        // Add a file token
        await cut.InvokeAsync(() => textarea.Input("@"));
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Add text
        await cut.InvokeAsync(() => textarea.Input("My message"));

        // Act
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert
        Assert.Contains("My message", sentMessage);
        Assert.Contains("FILE_CONTENT", sentMessage);
    }

    #endregion

    #region Event Subscription Tests

    [Fact]
    public void ShouldSubscribeToSessionChanged_OnInitialized()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>();

        // Assert - verify subscription by triggering event
        _mockChatService.Raise(x => x.SessionChanged += null,
            new PropertyChangedEventArgs(nameof(ConversationSession)));

        // Component should still render without errors
        Assert.NotNull(cut.Find(".chat-input-wrapper"));
    }

    [Fact]
    public void ShouldSubscribeToPropertyChanged_OnInitialized()
    {
        // Arrange & Act
        var cut = Render<AiChatInput>();

        // Assert - verify subscription by triggering event
        _commonOptions.SendCurrentFile = false;

        // Component should still render without errors
        Assert.NotNull(cut.Find(".chat-input-wrapper"));
    }

    [Fact]
    public void ShouldUnsubscribeEvents_OnDispose()
    {
        // Arrange
        var cut = Render<AiChatInput>();

        // Act
        cut.Dispose();

        // Assert - verify unsubscription by triggering events (should not throw)
        _mockChatService.Raise(x => x.SessionChanged += null,
            new PropertyChangedEventArgs(nameof(ConversationSession)));
        // No exception should be thrown
    }

    [Fact]
    public async Task ShouldUpdatePlaceholder_WhenModeChanges()
    {
        // Arrange
        _session.Mode = AppMode.Chat;
        var cut = Render<AiChatInput>();

        var textarea = cut.Find("textarea.text-input");
        Assert.Equal(SharedResource.InputPlaceholder, textarea.GetAttribute("placeholder"));

        // Act - change mode
        _session.Mode = AppMode.Agent;
        _mockChatService.Raise(x => x.SessionChanged += null,
            new PropertyChangedEventArgs(nameof(ConversationSession.Mode)));

        await cut.InvokeAsync(() => cut.Render());

        // Assert
        Assert.Equal(SharedResource.InputPlaceholderAgent, textarea.GetAttribute("placeholder"));
    }

    #endregion

    #region Cancel Button Tests

    [Fact]
    public async Task CancelButton_InvokesCancelCallback()
    {
        // Arrange
        var cancelCalled = false;

        var cut = Render<AiChatInput>(parameters => parameters
            .Add(p => p.IsLoading, true)
            .Add(p => p.Cancel, EventCallback.Factory.Create<string>(this, _ => cancelCalled = true)));

        // Act
        var cancelButton = cut.Find(".rz-chat-cancel-btn");
        await cut.InvokeAsync(() => cancelButton.Click());

        // Assert
        Assert.True(cancelCalled);
    }

    #endregion

    #region Open Rules/Skills Tests

    [Fact]
    public async Task OpenRulesButton_CallsVsBridge()
    {
        // Arrange
        _mockVsBridge.Setup(x => x.ExecuteToolAsync(
                BasicEnum.OpenFile,
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult());

        var cut = Render<AiChatInput>();

        // Find the rules button by icon
        var buttons = cut.FindAll("button.rz-button");
        var rulesButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("gavel"));

        // Act
        if (rulesButton != null)
        {
            await cut.InvokeAsync(() => rulesButton.Click());
        }

        // Assert
        _mockVsBridge.Verify(x => x.ExecuteToolAsync(
            BasicEnum.OpenFile,
            It.Is<Dictionary<string, object>>(d => d.ContainsKey("param1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenSkillsButton_CallsVsBridge()
    {
        // Arrange
        _mockVsBridge.Setup(x => x.ExecuteToolAsync(
                BasicEnum.OpenFolder,
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VsToolResult());

        var cut = Render<AiChatInput>();

        // Find the skills button by icon
        var buttons = cut.FindAll("button.rz-button");
        var skillsButton = buttons.FirstOrDefault(b => b.InnerHtml.Contains("extension"));

        // Act
        if (skillsButton != null)
        {
            await cut.InvokeAsync(() => skillsButton.Click());
        }

        // Assert
        _mockVsBridge.Verify(x => x.ExecuteToolAsync(
            BasicEnum.OpenFolder,
            It.Is<Dictionary<string, object>>(d => d.ContainsKey("param1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Mode Change Tests

    [Fact]
    public void ModeDropdown_IsBoundToSessionMode()
    {
        // Arrange
        _session.Mode = AppMode.Chat;

        // Act
        var cut = Render<AiChatInput>();
        var modeDropdown = cut.FindComponent<RadzenDropDown<AppMode>>();

        // Assert - dropdown value should match session mode
        Assert.Equal(AppMode.Chat, modeDropdown.Instance.Value);
    }

    [Fact]
    public void ModeDropdown_ReflectsSessionModeChanges()
    {
        // Arrange
        _session.Mode = AppMode.Chat;
        var cut = Render<AiChatInput>();

        // Act - change session mode
        _session.Mode = AppMode.Agent;
        cut.Render();

        // Assert - dropdown should reflect the change
        var modeDropdown = cut.FindComponent<RadzenDropDown<AppMode>>();
        Assert.Equal(AppMode.Agent, modeDropdown.Instance.Value);
    }

    #endregion

    #region GetFileIcon Tests

    [Theory]
    [InlineData(".cs", "📄")]
    [InlineData(".razor", "⚡")]
    [InlineData(".html", "🌐")]
    [InlineData(".css", "🎨")]
    [InlineData(".js", "📜")]
    [InlineData(".json", "📋")]
    [InlineData(".xml", "📋")]
    [InlineData(".txt", "📝")]
    [InlineData(".md", "📖")]
    [InlineData(".png", "🖼️")]
    [InlineData(".jpg", "🖼️")]
    [InlineData(".dll", "⚙️")]
    [InlineData(".exe", "⚙️")]
    [InlineData(".config", "🔧")]
    [InlineData(".csproj", "📦")]
    [InlineData(".sln", "📦")]
    [InlineData(".unknown", "📄")]
    public async Task FileChip_ShowsCorrectIcon_ForFileExtension(string extension, string expectedIcon)
    {
        // Arrange
        var fileName = $"Test{extension}";
        _vsCodeContext.SolutionFiles = [$"  📄 C:\\path\\{fileName}"];

        var cut = Render<AiChatInput>();
        var textarea = cut.Find("textarea.text-input");

        // Show hints
        await cut.InvokeAsync(() => textarea.Input("@"));

        // Act - select the file
        await cut.InvokeAsync(() => textarea.KeyDown("Enter"));

        // Assert
        var fileChip = cut.Find(".file-chip-stub");
        Assert.Equal(expectedIcon, fileChip.GetAttribute("data-icon"));
    }

    #endregion
}
