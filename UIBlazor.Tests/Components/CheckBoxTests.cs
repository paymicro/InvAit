namespace UIBlazor.Tests.Components;

/// <summary>
/// Tests for <see cref="CheckBox"/>
/// </summary>
public class CheckBoxTests : BunitContext
{
    public CheckBoxTests()
    {
        JSInterop.SetupVoid("Radzen.preventArrows", _ => true);
        Services.AddRadzenComponents();
    }

    [Fact]
    public void ShouldRenderCorrectTextAndValue()
    {
        // Arrange
        var text = "Enable Feature";
        var value = true;

        // Act
        var cut = Render<CheckBox>(parameters => parameters
            .Add(p => p.Text, text)
            .Add(p => p.Value, value));

        // Assert
        var label = cut.FindComponent<RadzenLabel>();
        Assert.Equal(text, label.Instance.Text);

        var radzenSwitch = cut.FindComponent<RadzenCheckBox<bool>>();
        Assert.Equal(value, radzenSwitch.Instance.Value);
    }

    [Fact]
    public void ShouldBeDisabledWhenDisabledParameterIsTrue()
    {
        // Act
        var cut = Render<CheckBox>(parameters => parameters
            .Add(p => p.Disabled, true));

        // Assert
        var radzenSwitch = cut.FindComponent<RadzenCheckBox<bool>>();
        Assert.True(radzenSwitch.Instance.Disabled);
    }
}
