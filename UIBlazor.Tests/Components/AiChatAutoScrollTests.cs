namespace UIBlazor.Tests.Components;

/// <summary>
/// The one-shot "pin to the bottom when the transcript becomes scrollable" watcher.
/// <para>
/// The observation itself lives in <c>app.js</c> (<c>watchChatOverflow</c>) and is not reachable
/// from bUnit. What the component owns — and what these tests pin down — is the contract around it:
/// arm it once on the first render with the current setting, re-arm it when the setting is toggled,
/// ignore every other setting, and detach it on dispose.
/// </para>
/// </summary>
public class AiChatAutoScrollTests : AiChatTestsBase
{
    /// <summary>
    /// Records every <c>watchChatOverflow</c> call. The returned list is LIVE (it grows as the
    /// component makes calls), so it must be projected with <see cref="EnabledFlags"/> at the point
    /// of assertion — copying it up front would freeze it at zero entries.
    /// </summary>
    private List<object?[]> CaptureAutoScrollWatchCalls() => CaptureJsVoidCalls("watchChatOverflow");

    /// <summary>The <c>enabled</c> argument of each captured <c>watchChatOverflow</c> call.</summary>
    private static List<bool> EnabledFlags(List<object?[]> calls)
        => [.. calls.Select(args => (bool)args[0]!)];

    [Fact]
    public async Task AutoScrollWatch_ArmedOnFirstRender_WithCurrentSetting()
    {
        // Arrange
        var calls = CaptureAutoScrollWatchCalls();

        // Act
        Render<AiChat>();
        await WaitUntilAsync(() => calls.Count > 0);

        // Assert - armed exactly once, with the setting's value (enabled by default).
        Assert.Equal([true], EnabledFlags(calls));
    }

    [Fact]
    public async Task AutoScrollWatch_Rearmed_WhenSettingIsToggled()
    {
        // Arrange
        var calls = CaptureAutoScrollWatchCalls();
        var cut = Render<AiChat>();
        await WaitUntilAsync(() => calls.Count == 1);

        // Act - the user unchecks the option in Settings.
        await cut.InvokeAsync(() =>
        {
            CommonOptions.AutoScrollOnOverflow = false;
            return Task.CompletedTask;
        });

        // Assert - the SAME watcher is re-armed with the new value; the chat is not restarted.
        await WaitUntilAsync(() => calls.Count == 2);
        Assert.Equal([true, false], EnabledFlags(calls));
    }

    [Fact]
    public async Task AutoScrollWatch_IgnoresUnrelatedSettings()
    {
        // Arrange
        var calls = CaptureAutoScrollWatchCalls();
        var cut = Render<AiChat>();
        await WaitUntilAsync(() => calls.Count == 1);

        // Act - a different Misc setting changes.
        await cut.InvokeAsync(() =>
        {
            CommonOptions.ShowMessageTimings = false;
            return Task.CompletedTask;
        });

        // Assert - no re-arm: the JS hop would be pure noise.
        Assert.Single(calls);
    }

    [Fact]
    public async Task AutoScrollWatch_StoppedOnDispose()
    {
        // Arrange - the observers are a JS-side leak on every WebView reload if left attached.
        // bUnit disposes the components of a context through DisposeComponentsAsync; calling
        // Dispose() on the rendered fragment only releases the fragment itself.
        var stopCalls = CaptureJsVoidCalls("stopChatOverflowWatch");

        Render<AiChat>();

        // Act
        await DisposeComponentsAsync();

        // Assert
        await WaitUntilAsync(() => stopCalls.Count == 1);
    }
}
