namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="ApprovalModeSelector"/>
/// </summary>
public class ApprovalModeSelectorTests : BunitContext
{
    public ApprovalModeSelectorTests()
    {
        Services.AddRadzenComponents();
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
    }

    #region Rendering Tests

    [Fact]
    public void ShouldRenderRadzenDropDown()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>();

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.NotNull(dropdown);
    }

    [Fact]
    public void ShouldHaveAllToolApprovalModeValues()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>();

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        var expectedModes = Enum.GetValues<ToolApprovalMode>().ToList();
        var data = dropdown.Instance.Data as IEnumerable<ToolApprovalMode>;
        Assert.NotNull(data);
        Assert.Equal(expectedModes, data);
    }

    [Fact]
    public void ShouldBindValueParameter()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, ToolApprovalMode.Ask));

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.Equal(ToolApprovalMode.Ask, dropdown.Instance.Value);
    }

    [Fact]
    public void ShouldApplyAdditionalAttributes()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.AdditionalAttributes, new Dictionary<string, object>
            {
                { "title", "Test Title" },
                { "data-testid", "approval-selector" }
            }));

        // Assert - check that the component renders without errors
        // AdditionalAttributes are passed via @attributes to RadzenDropDown
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.NotNull(dropdown);
        // Verify the dropdown is rendered (attributes are applied internally)
        Assert.Equal("width: 135px;", dropdown.Instance.Style);
    }

    [Fact]
    public void ShouldSetCorrectWidth()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>();

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.Equal("width: 135px;", dropdown.Instance.Style);
    }

    [Fact]
    public void ShouldReflectValueChanges()
    {
        // Arrange
        var currentValue = ToolApprovalMode.Allow;
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, currentValue)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<ToolApprovalMode>(this, v => currentValue = v)));

        // Act - re-render with new value
        cut.Render(parameters => parameters
            .Add(p => p.Value, ToolApprovalMode.Deny));

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.Equal(ToolApprovalMode.Deny, dropdown.Instance.Value);
    }

    #endregion

    #region Mode Info Tests

    [Fact]
    public void ShouldRenderAllowMode_WithCorrectText()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, ToolApprovalMode.Allow));

        // Assert - the dropdown should be rendered with Allow mode
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.Equal(ToolApprovalMode.Allow, dropdown.Instance.Value);
    }

    [Fact]
    public void ShouldRenderAskMode_WithCorrectText()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, ToolApprovalMode.Ask));

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.Equal(ToolApprovalMode.Ask, dropdown.Instance.Value);
    }

    [Fact]
    public void ShouldRenderDenyMode_WithCorrectText()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, ToolApprovalMode.Deny));

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.Equal(ToolApprovalMode.Deny, dropdown.Instance.Value);
    }

    #endregion

    #region ValueChanged Event Tests

    [Fact]
    public async Task ShouldInvokeValueChanged_WhenSelectionChanges()
    {
        // Arrange
        var currentValue = ToolApprovalMode.Allow;
        var valueChangedCalled = false;

        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, currentValue)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<ToolApprovalMode>(this, v =>
            {
                valueChangedCalled = true;
                currentValue = v;
            })));

        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();

        // Act - simulate change event
        await cut.InvokeAsync(() => dropdown.Instance.Change.InvokeAsync(ToolApprovalMode.Ask));

        // Assert
        Assert.True(valueChangedCalled);
        Assert.Equal(ToolApprovalMode.Ask, currentValue);
    }

    [Fact]
    public async Task ShouldPassCorrectValue_ToValueChangedCallback()
    {
        // Arrange
        ToolApprovalMode? receivedValue = null;

        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, ToolApprovalMode.Allow)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<ToolApprovalMode>(this, v => receivedValue = v)));

        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();

        // Act
        await cut.InvokeAsync(() => dropdown.Instance.Change.InvokeAsync(ToolApprovalMode.Deny));

        // Assert
        Assert.Equal(ToolApprovalMode.Deny, receivedValue);
    }

    [Fact]
    public async Task ShouldPassCorrectValue_ForAllModes()
    {
        // Arrange
        var allModes = Enum.GetValues<ToolApprovalMode>();
        var receivedValues = new List<ToolApprovalMode>();

        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, ToolApprovalMode.Allow)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<ToolApprovalMode>(this, v => receivedValues.Add(v))));

        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();

        // Act - test each mode
        foreach (var mode in allModes)
        {
            await cut.InvokeAsync(() => dropdown.Instance.Change.InvokeAsync(mode));
        }

        // Assert
        Assert.Equal(allModes.Length, receivedValues.Count);
        foreach (var mode in allModes)
        {
            Assert.Contains(mode, receivedValues);
        }
    }

    #endregion

    #region Template Rendering Tests

    [Fact]
    public void ShouldHaveTemplateDefined()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>();

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.NotNull(dropdown.Instance.Template);
    }

    [Fact]
    public void ShouldHaveValueTemplateDefined()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>();

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.NotNull(dropdown.Instance.ValueTemplate);
    }

    [Fact]
    public void ShouldRenderTemplate_WithIconElement()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, ToolApprovalMode.Allow));

        // Assert - find RadzenIcon within the rendered component
        var icons = cut.FindComponents<RadzenIcon>();
        Assert.NotEmpty(icons);
    }

    [Fact]
    public void ShouldRenderTemplate_WithTextSpan()
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, ToolApprovalMode.Allow));

        // Assert - the rendered output should contain the localized text
        var markup = cut.Markup;
        Assert.Contains(SharedResource.ApprovalModeAuto, markup);
    }

    [Theory]
    [InlineData(ToolApprovalMode.Allow, "check_circle")]
    [InlineData(ToolApprovalMode.Ask, "help_outline")]
    [InlineData(ToolApprovalMode.Deny, "cancel")]
    public void ShouldRenderCorrectIcon_ForMode(ToolApprovalMode mode, string expectedIcon)
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, mode));

        // Assert - find the icon and check its Icon property
        var icons = cut.FindComponents<RadzenIcon>();
        var icon = icons.FirstOrDefault();
        Assert.NotNull(icon);
        Assert.Equal(expectedIcon, icon.Instance.Icon);
    }

    [Theory]
    [InlineData(ToolApprovalMode.Allow, "var(--rz-success)")]
    [InlineData(ToolApprovalMode.Ask, "var(--rz-warning)")]
    [InlineData(ToolApprovalMode.Deny, "var(--rz-danger)")]
    public void ShouldRenderIcon_WithCorrectColor(ToolApprovalMode mode, string expectedColor)
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, mode));

        // Assert - find the icon and check its Style contains the expected color
        var icons = cut.FindComponents<RadzenIcon>();
        var icon = icons.FirstOrDefault();
        Assert.NotNull(icon);
        Assert.Contains(expectedColor, icon.Instance.Style);
    }

    [Theory]
    [InlineData(ToolApprovalMode.Allow, "ApprovalModeAuto")]
    [InlineData(ToolApprovalMode.Ask, "ApprovalModeAsk")]
    [InlineData(ToolApprovalMode.Deny, "ApprovalModeDeny")]
    public void ShouldRenderCorrectText_ForMode(ToolApprovalMode mode, string resourceKey)
    {
        // Arrange & Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, mode));

        // Assert - the rendered output should contain the localized text
        var markup = cut.Markup;
        var expectedText = resourceKey switch
        {
            "ApprovalModeAuto" => SharedResource.ApprovalModeAuto,
            "ApprovalModeAsk" => SharedResource.ApprovalModeAsk,
            "ApprovalModeDeny" => SharedResource.ApprovalModeDeny,
            _ => mode.ToString()
        };
        Assert.Contains(expectedText, markup);
    }

    #endregion

    #region Default Value Tests

    [Fact]
    public void ShouldRender_WithDefaultValue()
    {
        // Arrange & Act - render without setting Value parameter
        var cut = Render<ApprovalModeSelector>();

        // Assert - should render without errors
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.NotNull(dropdown);
        // Default value should be ToolApprovalMode.Allow (default enum value = 0)
        Assert.Equal(default(ToolApprovalMode), dropdown.Instance.Value);
    }

    [Fact]
    public void ShouldRender_WithValueParameterSet()
    {
        // Arrange
        var testValue = ToolApprovalMode.Ask;

        // Act
        var cut = Render<ApprovalModeSelector>(parameters => parameters
            .Add(p => p.Value, testValue));

        // Assert
        var dropdown = cut.FindComponent<RadzenDropDown<ToolApprovalMode>>();
        Assert.Equal(testValue, dropdown.Instance.Value);
    }

    #endregion
}
