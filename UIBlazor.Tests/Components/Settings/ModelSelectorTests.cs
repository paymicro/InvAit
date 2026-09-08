namespace UIBlazor.Tests.Components.Settings;

/// <summary>
/// Tests for <see cref="ModelSelector"/>
/// </summary>
public class ModelSelectorTests : BunitContext
{
    private readonly Mock<IProfileManager> _mockProfileManager;
    private readonly ConnectionProfile _profile;

    public ModelSelectorTests()
    {
        _mockProfileManager = new Mock<IProfileManager>();
        _profile = new ConnectionProfile
        {
            Model = "gpt-4o",
            AvailableModels = ["gpt-4o", "gpt-4o-mini", "claude-sonnet"]
        };
        _mockProfileManager.Setup(x => x.ActiveProfile).Returns(_profile);

        Services.AddSingleton(_mockProfileManager.Object);
        Services.AddRadzenComponents();

        // RadzenDropDown touches JS interop during lifecycle - silence via Moq
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

    private IRenderedComponent<ModelSelector> RenderSelector()
    {
        return Render<ModelSelector>();
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderDropDown_BoundToActiveProfileModel()
    {
        // Act
        var cut = RenderSelector();

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<string>>();
        Assert.Equal("gpt-4o", dropdown.Instance.Value);
    }

    [Fact]
    public void ShouldPassAvailableModels_AsData()
    {
        // Act
        var cut = RenderSelector();

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<string>>();
        var data = Assert.IsType<List<string>>(dropdown.Instance.Data);
        Assert.Equal(["gpt-4o", "gpt-4o-mini", "claude-sonnet"], data);
    }

    #endregion

    #region LoadData Filtering Tests

    [Fact]
    public async Task LoadData_WithFilter_ShowsOnlyMatchingModels()
    {
        // Arrange
        var cut = RenderSelector();
        var dropdown = cut.FindComponent<RadzenDropDown<string>>();

        // Act
        await cut.InvokeAsync(() => dropdown.Instance.LoadData.InvokeAsync(new LoadDataArgs { Filter = "gpt" }));
        cut.Render(); // re-render so updated viewModels flow into the Data parameter

        // Assert
        var data = Assert.IsType<List<string>>(cut.FindComponent<RadzenDropDown<string>>().Instance.Data);
        Assert.Equal(["gpt-4o", "gpt-4o-mini"], data);
    }

    [Fact]
    public async Task LoadData_WithEmptyFilter_RestoresAllModels()
    {
        // Arrange
        var cut = RenderSelector();
        var dropdown = cut.FindComponent<RadzenDropDown<string>>();

        await cut.InvokeAsync(() => dropdown.Instance.LoadData.InvokeAsync(new LoadDataArgs { Filter = "gpt" }));

        // Act
        await cut.InvokeAsync(() => dropdown.Instance.LoadData.InvokeAsync(new LoadDataArgs { Filter = null }));
        cut.Render();

        // Assert
        var data = Assert.IsType<List<string>>(cut.FindComponent<RadzenDropDown<string>>().Instance.Data);
        Assert.Equal(3, data.Count);
    }

    [Fact]
    public async Task LoadData_WithoutMatches_OffersAddNewCommand()
    {
        // Arrange
        var cut = RenderSelector();
        var dropdown = cut.FindComponent<RadzenDropDown<string>>();

        // Act - no model contains this filter -> single "add new" entry is offered
        await cut.InvokeAsync(() => dropdown.Instance.LoadData.InvokeAsync(new LoadDataArgs { Filter = "nonexistent" }));
        cut.Render();

        // Assert
        var data = Assert.IsType<List<string>>(cut.FindComponent<RadzenDropDown<string>>().Instance.Data);
        var item = Assert.Single(data);
        Assert.Equal("➕ nonexistent", item);
    }

    #endregion

    #region Change (Add New Model) Tests

    [Fact]
    public async Task SelectingAddNewCommand_SetsProfileModel_ToCleanName()
    {
        // Arrange
        var cut = RenderSelector();
        var dropdown = cut.FindComponent<RadzenDropDown<string>>();

        // Act - user picks the synthesized "➕ new-model" command
        await cut.InvokeAsync(() => dropdown.Instance.ValueChanged.InvokeAsync("➕ new-model"));

        // Assert
        Assert.Equal("new-model", _profile.Model);
    }

    [Fact]
    public async Task SelectingRegularModel_DoesNotTouchProfileViaChangeHandler()
    {
        // Arrange
        _profile.Model = "---";
        var cut = RenderSelector();
        var dropdown = cut.FindComponent<RadzenDropDown<string>>();

        // Act - plain model names are handled by @bind-Value, not the change handler
        await cut.InvokeAsync(() => dropdown.Instance.Change.InvokeAsync("claude-sonnet"));

        // Assert
        Assert.Equal("---", _profile.Model);
    }

    [Fact]
    public async Task ChangeHandler_IgnoresNullValue()
    {
        // Arrange
        var cut = RenderSelector();

        // Act & Assert - must not throw on null selection
        var exception = await Record.ExceptionAsync(() =>
            cut.InvokeAsync(() =>
                cut.FindComponent<RadzenDropDown<string>>().Instance.Change.InvokeAsync(null)));

        Assert.Null(exception);
        Assert.Equal("gpt-4o", _profile.Model);
    }

    #endregion

    #region External Update Tests

    [Fact]
    public async Task ProfilePropertyChanged_AvailableModels_RerendersWithoutErrors()
    {
        // Arrange
        var cut = RenderSelector();

        // Act - e.g. models refreshed from API
        _profile.AvailableModels = ["new-model-a"];

        var exception = await Record.ExceptionAsync(() =>
            cut.InvokeAsync(() => _mockProfileManager.Raise(
                x => x.PropertyChanged += null,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(ConnectionProfile.AvailableModels)))));
        cut.Render();

        // Assert
        Assert.Null(exception);
        var data = Assert.IsType<List<string>>(cut.FindComponent<RadzenDropDown<string>>().Instance.Data);
        Assert.Single(data);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Arrange
        var cut = RenderSelector();

        // Act & Assert
        var exception = Record.Exception(() => cut.Dispose());
        Assert.Null(exception);
    }

    #endregion
}
