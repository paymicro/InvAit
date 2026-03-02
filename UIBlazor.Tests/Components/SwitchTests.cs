using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using UIBlazor.Components;

namespace UIBlazor.Tests.Components;

public class SwitchTests : Bunit.TestContext
{
    public SwitchTests()
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
        var cut = RenderComponent<Switch>(parameters => parameters
            .Add(p => p.Text, text)
            .Add(p => p.Value, value));

        // Assert
        var label = cut.FindComponent<RadzenLabel>();
        Assert.Equal(text, label.Instance.Text);

        var radzenSwitch = cut.FindComponent<RadzenSwitch>();
        Assert.Equal(value, radzenSwitch.Instance.Value);
    }

    [Fact]
    public async Task ShouldTriggerValueChangedWhenSwitched()
    {
        // Arrange
        bool? triggeredValue = null;
        var cut = RenderComponent<Switch>(parameters => parameters
            .Add(p => p.Value, false)
            .Add(p => p.ValueChanged, EventCallback.Factory.Create<bool>(this, v => triggeredValue = v)));

        // Act
        var radzenSwitch = cut.FindComponent<RadzenSwitch>();
        await cut.InvokeAsync(() => radzenSwitch.Instance.ValueChanged.InvokeAsync(true));

        // Assert
        Assert.True(triggeredValue);
    }

    [Fact]
    public void ShouldBeDisabledWhenDisabledParameterIsTrue()
    {
        // Act
        var cut = RenderComponent<Switch>(parameters => parameters
            .Add(p => p.Disabled, true));

        // Assert
        var radzenSwitch = cut.FindComponent<RadzenSwitch>();
        Assert.True(radzenSwitch.Instance.Disabled);
    }
}
