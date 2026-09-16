namespace UIBlazor.Tests.Utils;

using System.Reflection;
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

    /// <summary>
    /// Harness with render counting and configurable interval.
    /// RenderCount is incremented in BuildRenderTree (actual render).
    /// OnRenderedOrder is set in OnRendered, BuildRenderTreeOrder in BuildRenderTree,
    /// so tests can verify call ordering.
    /// </summary>
    private abstract class CountingHarness : ThrottledComponentBase
    {
        public int RenderCount { get; private set; }
        public int OnRenderedOrder { get; private set; }
        public int BuildRenderTreeOrder { get; private set; }
        private int _callOrder;
        private bool _changed = true;

        public void MarkChanged() => _changed = true;

        protected override bool HasChanges() => _changed;
        protected override void OnRendered()
        {
            OnRenderedOrder = ++_callOrder;
            _changed = false;
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            BuildRenderTreeOrder = ++_callOrder;
            RenderCount++;
            builder.AddMarkupContent(0, "content");
        }
    }

    /// <summary>5-second interval — no timing pressure for CTS inspection tests.</summary>
    private class LongIntervalHarness : CountingHarness
    {
        protected override int RenderIntervalMs => 5000;
    }

    /// <summary>500ms interval — matches default, used for timing-sensitive tests.</summary>
    private class MediumIntervalHarness : CountingHarness
    {
        protected override int RenderIntervalMs => 500;
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
            await Task.Delay(100, TestContext.Current.CancellationToken);
            cut.Instance.Content = Version(i);
            cut.Instance.OnParametersPushed();
            cut.Render(parameters => { });
        }

        // Settle: wait past the interval for the trailing render
        await Task.Delay(800, TestContext.Current.CancellationToken);

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
            await Task.Delay(100, TestContext.Current.CancellationToken);
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
        await Task.Delay(800, TestContext.Current.CancellationToken);

        // Assert - final content must appear despite the identical re-render
        cut.WaitForAssertion(
            () => Assert.Contains("v6", cut.Markup),
            TimeSpan.FromSeconds(2));
    }

    // ----------------------------------------------------------------
    //  Throttle internals tests
    //  Blazor does NOT call ShouldRender() on the initial render.
    //  The _shouldRender flag is consumed on the 2nd render (first
    //  ShouldRender call). Throttle logic kicks in from the 3rd render on.
    // ----------------------------------------------------------------

    [Fact]
    public async Task Throttle_ReplacedCts_IsDisposed()
    {
        var cut = Render<LongIntervalHarness>();
        Assert.Equal(1, cut.Instance.RenderCount);

        // Burn the first ShouldRender call
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });
        Assert.Equal(2, cut.Instance.RenderCount);
        Assert.Null(cut.Instance.PendingCts);

        // First throttle hit
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });
        var firstCts = cut.Instance.PendingCts;
        Assert.NotNull(firstCts);
        Assert.False(firstCts.IsCancellationRequested);

        // Second throttle hit — replaces PendingCts
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });
        var secondCts = cut.Instance.PendingCts;
        Assert.NotSame(firstCts, secondCts);

        // Old CTS must be canceled and disposed
        Assert.True(firstCts.IsCancellationRequested);
        Assert.True(IsCtsDisposed(firstCts));

        cut.Instance.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnthrottledRender_PendingCts_IsCanceled()
    {
        var cut = Render<MediumIntervalHarness>();
        Assert.Equal(1, cut.Instance.RenderCount);

        // Burn the first ShouldRender call
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });
        Assert.Equal(2, cut.Instance.RenderCount);

        // Throttle path — schedules delayed render
        await Task.Delay(100, TestContext.Current.CancellationToken);
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });
        Assert.Equal(2, cut.Instance.RenderCount);
        Assert.NotNull(cut.Instance.PendingCts);
        Assert.False(cut.Instance.PendingCts.IsCancellationRequested);

        // Unthrottled render (elapsed >= interval)
        await Task.Delay(450, TestContext.Current.CancellationToken);
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });
        Assert.Equal(3, cut.Instance.RenderCount);

        // Pending CTS must be canceled after unthrottled render
        var cts = cut.Instance.PendingCts;
        if (cts is not null)
            Assert.True(cts.IsCancellationRequested);

        cut.Instance.Dispose();
    }

    [Fact]
    public async Task UnthrottledRender_NoRedundantTrailingRender()
    {
        var cut = Render<MediumIntervalHarness>();
        Assert.Equal(1, cut.Instance.RenderCount);

        // Burn the first ShouldRender call
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });
        Assert.Equal(2, cut.Instance.RenderCount);

        // Throttle — schedule delayed render
        await Task.Delay(100, TestContext.Current.CancellationToken);
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });
        Assert.Equal(2, cut.Instance.RenderCount);

        // Unthrottled render (550 >= 500)
        await Task.Delay(450, TestContext.Current.CancellationToken);
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });
        Assert.Equal(3, cut.Instance.RenderCount);

        var renderCountAfterUnthrottled = cut.Instance.RenderCount;

        // Wait past the delayed callback deadline
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // No redundant render should occur
        Assert.Equal(renderCountAfterUnthrottled, cut.Instance.RenderCount);

        cut.Instance.Dispose();
    }

    [Fact]
    public void OnRendered_CalledBeforeBuildRenderTree()
    {
        var cut = Render<LongIntervalHarness>();

        // Initial render: ShouldRender NOT called
        Assert.Equal(0, cut.Instance.OnRenderedOrder);
        Assert.Equal(1, cut.Instance.BuildRenderTreeOrder);

        // Second render: ShouldRender IS called
        cut.Instance.MarkChanged();
        cut.Render(parameters => { });

        Assert.Equal(2, cut.Instance.OnRenderedOrder);
        Assert.Equal(3, cut.Instance.BuildRenderTreeOrder);
        Assert.True(cut.Instance.OnRenderedOrder < cut.Instance.BuildRenderTreeOrder);

        cut.Instance.Dispose();
    }

    /// <summary>
    /// CancellationTokenSource.IsDisposed is internal in .NET.
    /// Use reflection to check disposal state for test assertions.
    /// </summary>
    private static bool IsCtsDisposed(CancellationTokenSource cts)
    {
        var field = typeof(CancellationTokenSource)
            .GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? typeof(CancellationTokenSource)
                .GetField("m_disposed", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is not null)
        {
            var value = field.GetValue(cts);
            if (value is int intVal) return intVal != 0;
            if (value is bool boolVal) return boolVal;
        }
        // Fallback: a disposed CTS throws ObjectDisposedException on Cancel()
        try
        {
            cts.Cancel();
            return false; // still alive
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
