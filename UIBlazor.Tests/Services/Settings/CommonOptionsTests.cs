namespace UIBlazor.Tests.Services.Settings;

/// <summary>
/// Defaults of <see cref="CommonOptions"/>. They are the product decision, not an implementation
/// detail: a default that silently differs from the documented one is invisible until a user
/// reports "the chat does not follow the answer".
/// </summary>
public class CommonOptionsTests
{
    [Fact]
    public void AutoScrollOnOverflow_IsEnabledByDefault()
    {
        // Arrange & Act
        var options = new CommonOptions();

        // Assert - the transcript pins itself to the bottom once it becomes scrollable, unless the
        // user turns it off.
        Assert.True(options.AutoScrollOnOverflow);
    }

    [Fact]
    public void AutoScrollOnOverflow_RaisesPropertyChanged()
    {
        // Arrange
        var options = new CommonOptions();
        string? raised = null;
        options.PropertyChanged += (_, e) => raised = e.PropertyName;

        // Act
        options.AutoScrollOnOverflow = false;

        // Assert - the settings provider persists on this event, and AiChat re-arms its JS watcher
        // on it; a silent set would leave both behind.
        Assert.Equal(nameof(CommonOptions.AutoScrollOnOverflow), raised);
        Assert.False(options.AutoScrollOnOverflow);
    }
}
