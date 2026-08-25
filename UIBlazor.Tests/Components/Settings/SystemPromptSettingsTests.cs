namespace UIBlazor.Tests.Components.Settings;

/// <summary>
/// Tests for <see cref="SystemPromptSettings"/>
/// </summary>
public class SystemPromptSettingsTests : BunitContext
{
    private readonly Mock<IProfileManager> _mockProfileManager;
    private readonly ConnectionProfile _profile;

    public SystemPromptSettingsTests()
    {
        _mockProfileManager = new Mock<IProfileManager>();
        _profile = new ConnectionProfile();
        _mockProfileManager.Setup(x => x.ActiveProfile).Returns(_profile);

        Services.AddSingleton(_mockProfileManager.Object);
        Services.AddRadzenComponents();

        // RadzenCheckBoxList touches JS interop during lifecycle - silence via Moq
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

    private IRenderedComponent<SystemPromptSettings> RenderSettings()
    {
        return Render<SystemPromptSettings>();
    }

    private static readonly string[] AllLabels =
    [
        SharedResource.PromptSectionActiveFile,
        SharedResource.PromptSectionSolutionStructure,
        SharedResource.PromptSectionCurrentDate,
        SharedResource.PromptSectionMermaid,
        SharedResource.PromptSectionRules,
        SharedResource.PromptSectionAgents,
        SharedResource.PromptSectionSkills,
        SharedResource.PromptSectionModeInstructions
    ];

    #region Rendering Tests

    [Fact]
    public void ShouldRenderAllEightPromptSections()
    {
        // Act
        var cut = RenderSettings();

        // Assert
        var items = cut.FindComponents<RadzenCheckBoxListItem<int>>();
        Assert.Equal(8, items.Count);
        foreach (var label in AllLabels)
        {
            Assert.Contains(label, cut.Markup);
        }
    }

    [Fact]
    public void ShouldRenderTextArea_BoundToSystemPrompt()
    {
        // Arrange
        _profile.SystemPrompt = "You are a senior developer.";

        // Act
        var cut = RenderSettings();

        // Assert
        var textArea = cut.FindComponent<RadzenTextArea>();
        Assert.Equal("You are a senior developer.", textArea.Instance.Value);
        Assert.Contains(SharedResource.CustomSystemPrompt, cut.Markup);
    }

    [Fact]
    public void ShouldRenderSectionHeaders()
    {
        // Act
        var cut = RenderSettings();

        // Assert
        Assert.Contains(SharedResource.PromptSections, cut.Markup);
    }

    #endregion

    #region Selection Sync Tests

    [Fact]
    public async Task OnChange_Selection_MapsIndicesToCorrectFlags()
    {
        // Arrange - indices: 0 SendCurrentFile, 1 SendSolutionStructure, 2 SendCurrentDate,
        //                    3 UseMermaidDiagrams, 4 SendRules, 5 SendAgentsMd,
        //                    6 SendSkills, 7 SendModeInstructions
        var cut = RenderSettings();
        var list = cut.FindComponent<RadzenCheckBoxList<int>>();

        // Act - user keeps only "Active file" and "Current date"
        await cut.InvokeAsync(() => list.Instance.Change.InvokeAsync(new[] { 0, 2 }));

        // Assert
        Assert.True(_profile.SendCurrentFile);
        Assert.True(_profile.SendCurrentDate);
        Assert.False(_profile.SendSolutionStructure);
        Assert.False(_profile.UseMermaidDiagrams);
        Assert.False(_profile.SendRules);
        Assert.False(_profile.SendAgentsMd);
        Assert.False(_profile.SendSkills);
        Assert.False(_profile.SendModeInstructions);
    }

    [Fact]
    public async Task OnChange_EmptySelection_DisablesAllFlags()
    {
        // Arrange
        var cut = RenderSettings();
        var list = cut.FindComponent<RadzenCheckBoxList<int>>();

        // Act
        await cut.InvokeAsync(() => list.Instance.Change.InvokeAsync(Array.Empty<int>()));

        // Assert
        Assert.False(_profile.SendCurrentFile);
        Assert.False(_profile.SendModeInstructions);
    }

    [Fact]
    public void InitialSelection_MatchesDefaultProfile_AllFlagsTrueByDefault()
    {
        // Act
        var cut = RenderSettings();

        // Assert - all profile flags default to true -> all 8 items selected
        var list = cut.FindComponent<RadzenCheckBoxList<int>>();
        var selected = list.Instance.Value?.ToList() ?? [];
        Assert.Equal(Enumerable.Range(0, 8), selected);
    }

    [Fact]
    public void InitialSelection_ExcludesDisabledFlags()
    {
        // Arrange
        _profile.SendRules = false;
        _profile.SendSkills = false;

        // Act
        var cut = RenderSettings();

        // Assert - indices 4 and 6 must be missing
        var list = cut.FindComponent<RadzenCheckBoxList<int>>();
        var selected = list.Instance.Value?.ToList() ?? [];
        Assert.Equal(new[] { 0, 1, 2, 3, 5, 7 }, selected);
    }

    #endregion

    #region External Property Changed Tests

    [Fact]
    public async Task ProfilePropertyChanged_OnFlagProperty_ResyncsSelection()
    {
        // Arrange
        var cut = RenderSettings();

        // Act - another UI element toggles a flag externally
        _profile.UseMermaidDiagrams = false;

        var exception = await Record.ExceptionAsync(() =>
            cut.InvokeAsync(() => _mockProfileManager.Raise(
                x => x.PropertyChanged += null,
                new System.ComponentModel.PropertyChangedEventArgs("UseMermaidDiagrams"))));

        // Assert - selection now excludes mermaid (index 3)
        var list = cut.FindComponent<RadzenCheckBoxList<int>>();
        var selected = list.Instance.Value?.ToList() ?? [];
        Assert.DoesNotContain(3, selected);
        Assert.Null(exception);
    }

    [Fact]
    public async Task ProfilePropertyChanged_ResyncsOnlyTrackedFlags()
    {
        // Arrange - all flags true by default -> all 8 selected
        var cut = RenderSettings();

        // Act 1 - flag changed externally, but only an UNRELATED event is raised
        _profile.SendRules = false;
        await cut.InvokeAsync(() => _mockProfileManager.Raise(
            x => x.PropertyChanged += null,
            new System.ComponentModel.PropertyChangedEventArgs("Temperature")));

        // Assert 1 - selection was NOT resynced: SendRules (index 4) still present
        var list = cut.FindComponent<RadzenCheckBoxList<int>>();
        Assert.Contains(4, list.Instance.Value?.ToList() ?? []);

        // Act 2 - the tracked property change arrives
        await cut.InvokeAsync(() => _mockProfileManager.Raise(
            x => x.PropertyChanged += null,
            new System.ComponentModel.PropertyChangedEventArgs("SendRules")));

        // Assert 2 - selection resynced from profile: index 4 dropped
        list = cut.FindComponent<RadzenCheckBoxList<int>>();
        Assert.DoesNotContain(4, list.Instance.Value?.ToList() ?? []);
    }

    #endregion

    #region Lifecycle Tests

    [Fact]
    public async Task TextAreaEdit_BindsBackToProfileSystemPrompt()
    {
        // Arrange
        var cut = RenderSettings();
        var textArea = cut.FindComponent<RadzenTextArea>();

        // Act
        await cut.InvokeAsync(() => textArea.Instance.ValueChanged.InvokeAsync("new prompt"));

        // Assert
        Assert.Equal("new prompt", _profile.SystemPrompt);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Arrange
        var cut = RenderSettings();

        // Act & Assert
        var exception = Record.Exception(() => cut.Dispose());
        Assert.Null(exception);
    }

    #endregion
}
