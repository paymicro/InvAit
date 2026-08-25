namespace UIBlazor.Tests.Components.Settings;

/// <summary>
/// Tests for <see cref="ExtraHeaderPicker"/>
/// </summary>
public class ExtraHeaderPickerTests : BunitContext
{
    private readonly Mock<IProfileManager> _mockProfileManager;
    private readonly ConnectionProfile _profile;

    public ExtraHeaderPickerTests()
    {
        _mockProfileManager = new Mock<IProfileManager>();
        _profile = new ConnectionProfile
        {
            ApiKey = "sk-test",
            ExtraHeaders =
            [
                new HeaderModel { Name = "X-Custom", Value = "42" },
                new HeaderModel { Name = "X-Trace", Value = "abc" }
            ]
        };
        _mockProfileManager.Setup(x => x.ActiveProfile).Returns(_profile);

        Services.AddSingleton(_mockProfileManager.Object);
        Services.AddRadzenComponents();

        // RadzenDataGrid calls JS interop during lifecycle - silence it via Moq
        var mockJsRuntime = new Mock<IJSRuntime>();
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        mockJsRuntime
            .Setup(x => x.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .ReturnsAsync((IJSObjectReference?)null!);
        Services.AddSingleton(mockJsRuntime.Object);

        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
    }

    private IReadOnlyList<IRenderedComponent<RadzenButton>> GetGridButtons(IRenderedComponent<ExtraHeaderPicker> cut)
    {
        return cut.FindComponents<RadzenButton>().ToList();
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderTitle_AndColumnHeaders()
    {
        // Act
        var cut = Render<ExtraHeaderPicker>();

        // Assert
        Assert.Contains(SharedResource.ExtraHeaders, cut.Markup);
        Assert.Contains(SharedResource.Header, cut.Markup);
        Assert.Contains(SharedResource.Value, cut.Markup);
    }

    [Fact]
    public void ShouldRenderRowPerHeader_WithBoundValues()
    {
        // Act
        var cut = Render<ExtraHeaderPicker>();

        // Assert - two headers -> four text boxes (Name + Value per row)
        var textBoxes = cut.FindComponents<RadzenTextBox>();
        Assert.Equal(4, textBoxes.Count);
        Assert.Contains(textBoxes, tb => tb.Instance.Value == "X-Custom");
        Assert.Contains(textBoxes, tb => tb.Instance.Value == "42");
        Assert.Contains(textBoxes, tb => tb.Instance.Value == "X-Trace");
    }

    [Fact]
    public void EmptyTemplate_ShowsLabel_WhenApiKeyPresent()
    {
        // Arrange - remove all rows so EmptyTemplate kicks in
        _profile.ExtraHeaders.Clear();

        // Act
        var cut = Render<ExtraHeaderPicker>();

        // Assert
        Assert.Contains(SharedResource.ExtraHeadersIsEmpty, cut.Markup);
    }

    [Fact]
    public void EmptyTemplate_HidesLabel_WhenApiKeyAbsent()
    {
        // Arrange
        _profile.ExtraHeaders.Clear();
        _profile.ApiKey = string.Empty;

        // Act
        var cut = Render<ExtraHeaderPicker>();

        // Assert
        Assert.DoesNotContain(SharedResource.ExtraHeadersIsEmpty, cut.Markup);
    }

    #endregion

    #region Interaction Tests

    [Fact]
    public async Task AddButton_AppendsHeader_AndTriggersSave()
    {
        // Arrange
        var cut = Render<ExtraHeaderPicker>();
        var addButtonsBefore = _profile.ExtraHeaders.Count;

        // Act
        var addButton = GetGridButtons(cut).First(b => b.Instance.Icon == "add");
        await cut.InvokeAsync(() => addButton.Find("button").Click());

        // Assert
        Assert.Equal(addButtonsBefore + 1, _profile.ExtraHeaders.Count);
        _mockProfileManager.Verify(x => x.CallSaveTrigger(), Times.Once);
    }

    [Fact]
    public async Task DeleteButton_RemovesHeader_AndTriggersSave()
    {
        // Arrange
        var cut = Render<ExtraHeaderPicker>();
        var target = _profile.ExtraHeaders[0];

        // Act
        var deleteButton = GetGridButtons(cut).First(b => b.Instance.Icon == "delete");
        await cut.InvokeAsync(() => deleteButton.Find("button").Click());

        // Assert
        Assert.DoesNotContain(target, _profile.ExtraHeaders);
        Assert.Single(_profile.ExtraHeaders);
        _mockProfileManager.Verify(x => x.CallSaveTrigger(), Times.Once);
    }

    [Fact]
    public async Task EditingTextBox_BindsBackToHeaderModel()
    {
        // Arrange
        var cut = Render<ExtraHeaderPicker>();
        var nameBox = cut.FindComponents<RadzenTextBox>().First(tb => tb.Instance.Value == "X-Custom");

        // Act - ValueChanged is wired by @bind-Value to header.Name
        await cut.InvokeAsync(() => nameBox.Instance.ValueChanged.InvokeAsync("X-Renamed"));

        // Assert
        Assert.Equal("X-Renamed", _profile.ExtraHeaders[0].Name);
    }

    [Fact]
    public async Task TextBoxChangeEvent_TriggersProfileSave()
    {
        // Arrange
        var cut = Render<ExtraHeaderPicker>();
        var nameBox = cut.FindComponents<RadzenTextBox>().First(tb => tb.Instance.Value == "X-Custom");

        // Act - Change is wired to ProfileManager.CallSaveTrigger
        await cut.InvokeAsync(() => nameBox.Instance.Change.InvokeAsync("anything"));

        // Assert
        _mockProfileManager.Verify(x => x.CallSaveTrigger(), Times.Once);
    }

    #endregion

    #region Event Subscription Tests

    [Fact]
    public async Task OnSavedEvent_RerendersWithoutErrors()
    {
        // Arrange
        var cut = Render<ExtraHeaderPicker>();

        // Act & Assert - raising save-completed event must not throw
        var exception = await Record.ExceptionAsync(() =>
            cut.InvokeAsync(() => _mockProfileManager.Raise(x => x.OnSaved += null)));
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Arrange
        var cut = Render<ExtraHeaderPicker>();

        // Act & Assert
        var exception = Record.Exception(() =>
        {
            cut.Dispose();
            _mockProfileManager.Raise(x => x.OnSaved += null);
        });
        Assert.Null(exception);
    }

    #endregion
}
