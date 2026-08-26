namespace UIBlazor.Tests.Utils;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using UIBlazor.Components;

/// <summary>
/// Tests for <see cref="ThrottledComponentBase"/> trailing-update delivery
/// during rapid parameter streaming (e.g. SSE token bursts).
/// </summary>
public class ThrottledComponentBaseTests : BunitContext
{
    /// <summary>
    /// Harness mimicking how tool views consume the base class:
    /// HasChanges gates on parsed data (like DiffView.ParseDiff's
    /// sumChars &gt; processedChars check), OnRendered resets the flag.
    /// </summary>
    private class StreamHarness : ThrottledComponentBase
    {
        public string Content { get; set; } = string.Empty;
        private int _parsedLength;
        private bool _changed = true;

        /// <summary>
        /// Mirrors DiffView.OnParametersSet: called on every parameter push,
        /// marks changed only when the parsed payload grew.
        /// </summary>
        public void OnParametersPushed()
        {
            _changed = Content.Length > _parsedLength;
            if (!_changed)
            {
                return;
            }
            _parsedLength = Content.Length;
        }

        protected override bool HasChanges() => _changed;
        protected override void OnRendered() => _changed = false;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddMarkupContent(0, Content);
        }
    }

    [Fact]
    public async Task Streaming_BurstThenSettle_FinalContentIsDisplayed()
    {
        // Arrange - stream 10 versions, each arriving inside the throttle window,
        // then go quiet. The final version MUST appear once the window passes.
        var cut = Render<StreamHarness>();
        cut.Instance.Content = "v0";
        cut.Instance.OnParametersPushed();
        cut.Render(parameters => { });
        Assert.Contains("v0", cut.Markup);

        // Act - simulate token stream faster than the 500ms render interval
        string Version(int n) => $"v{n}-" + new string('x', n * 20);
        cut.Instance.Content = Version(6); // remember the final version
        for (var i = 1; i <= 6; i++)
        {
            await Task.Delay(100);
            cut.Instance.Content = Version(i);
            cut.Instance.OnParametersPushed();
            cut.Render(parameters => { });
        }

        // Settle: wait past the interval for the trailing render
        await Task.Delay(800);

        // Assert
        cut.WaitForAssertion(
            () => Assert.Contains("v6", cut.Markup),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Streaming_IdenticalReRenderAfterBurst_FinalContentIsStillDisplayed()
    {
        // Reproduces production flow: when the SSE stream completes, ChatService
        // bumps the message state -> parent cascade re-renders the tool view with
        // the SAME arguments. DiffView.ParseDiff then computes _changed=false,
        // and when the pending delayed render fires, the HasChanges() veto skips
        // it — the final streamed chunk is never displayed.
        var cut = Render<StreamHarness>();
        cut.Instance.Content = "v0";
        cut.Instance.OnParametersPushed();
        cut.Render(parameters => { });
        Assert.Contains("v0", cut.Markup);

        // Burst of streamed versions (each grows, like appended SSE tokens),
        // all inside the throttle window
        string Version(int n) => $"v{n}-" + new string('x', n * 20);
        cut.Instance.Content = Version(6); // remember the final version
        for (var i = 1; i <= 6; i++)
        {
            await Task.Delay(100);
            cut.Instance.Content = Version(i);
            cut.Instance.OnParametersPushed();
            cut.Render(parameters => { });
        }

        // Stream completed: identical parameter values are pushed again
        // (same string -> ParseDiff sees no growth -> _changed=false)
        cut.Instance.Content = Version(6);
        cut.Instance.OnParametersPushed();
        cut.Render(parameters => { });

        // Settle: wait past the interval for the trailing render
        await Task.Delay(800);

        // Assert - final content must appear despite the identical re-render
        cut.WaitForAssertion(
            () => Assert.Contains("v6", cut.Markup),
            TimeSpan.FromSeconds(2));
    }
}
