namespace UIBlazor.Tests.Components.ToolViews;

/// <summary>
/// Tests for <see cref="MarkdownBlock"/>
/// </summary>
public class MarkdownBlockTests : BunitContext
{
    private const string _jsFunction = "renderMarkdownToElement";

    public MarkdownBlockTests()
    {
        JSInterop.SetupVoid(_jsFunction, _ => true);
    }

    [Fact]
    public void ShouldRenderContainerDiv_WithUniqueMarkerId()
    {
        // Act
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "# Hello"));

        // Assert
        var div = cut.Find(".markdown-content");
        Assert.NotNull(div);
        Assert.StartsWith("md-", div.Id);
        // The element itself stays empty; markdown is rendered into it by JS
        Assert.Equal(string.Empty, div.TextContent);
    }

    [Fact]
    public void ShouldCallJs_ToRenderContent_AfterRender()
    {
        // Act
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "**bold text**"));

        // Assert - VerifyInvoke(name) returns the single invocation
        var invocation = JSInterop.VerifyInvoke(_jsFunction);
        Assert.True(invocation.Arguments.Count >= 2);
        var blockId = (string)invocation.Arguments[0];
        Assert.StartsWith("md-", blockId);
        // The id passed to JS must match the rendered container id
        Assert.Equal(cut.Find(".markdown-content").Id, blockId);
    }

    [Fact]
    public void ShouldNotCallJs_WhenContentEmpty()
    {
        // Act - empty content equals the initial "last rendered" state,
        // so ShouldRender returns false and no JS interop happens
        var cut = Render<MarkdownBlock>();

        // Assert - VerifyInvoke throws when the function was never called
        Assert.Throws<JSInvokeCountExpectedException>(() => JSInterop.VerifyInvoke(_jsFunction));
    }

    [Fact]
    public void ShouldCallJsAgain_WhenContentChanges_NotStreaming()
    {
        // Default (IsStreaming = false): every change renders. That is the SAFE default - a caller
        // that does not stream keeps the original behaviour, and nothing can be left stale.
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "first"));

        cut.Render(parameters => parameters
            .Add(p => p.Content, "second"));

        var invocations = JSInterop.Invocations[_jsFunction];
        Assert.Equal(2, invocations.Count);
        Assert.Equal("second", invocations[1].Arguments[1]);
    }

    [Fact]
    public async Task Streaming_SmallGrowth_SkipsImmediateRenderButFlushesByCeiling()
    {
        // Below the character gate the render is not done SYNCHRONOUSLY, but it may not wait
        // forever either: a slow model would otherwise leave the block frozen. The ceiling timer
        // flushes the pending text.
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "start")
            .Add(p => p.IsStreaming, true));

        Assert.Single(JSInterop.Invocations[_jsFunction]);

        // Act - 10 more characters, far below the gate.
        cut.Render(parameters => parameters
            .Add(p => p.Content, "start + ten")
            .Add(p => p.IsStreaming, true));

        // Assert - not rendered synchronously...
        Assert.Single(JSInterop.Invocations[_jsFunction]);

        // ...but flushed once the ceiling expires.
        await WaitForRenderAsync(2);
        Assert.Equal("start + ten", JSInterop.Invocations[_jsFunction][^1].Arguments[1]);
    }

    [Fact]
    public void Streaming_GrowthAboveGate_RendersImmediately()
    {
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "start")
            .Add(p => p.IsStreaming, true));

        // Act - enough new text accumulated to cross the gate, no waiting for the ceiling.
        var grown = "start" + new string('x', 120);
        cut.Render(parameters => parameters
            .Add(p => p.Content, grown)
            .Add(p => p.IsStreaming, true));

        // Assert
        var invocations = JSInterop.Invocations[_jsFunction];
        Assert.Equal(2, invocations.Count);
        Assert.Equal(grown, invocations[1].Arguments[1]);
    }

    [Fact]
    public async Task TextStopsGrowing_StillRenders_WithoutWaitingForStreamEnd()
    {
        // The reported defect: text is below the gate, then the model stops writing text and
        // starts emitting tool calls. The block's Content never changes again while the MESSAGE is
        // still streaming - so nothing would ever trigger the render and the tail was missing until
        // the whole run finished.
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "partial answer")
            .Add(p => p.IsStreaming, true));

        cut.Render(parameters => parameters
            .Add(p => p.Content, "partial answer, tail")
            .Add(p => p.IsStreaming, true));

        // Act - no further changes at all (tool calling phase).
        await WaitForRenderAsync(2);

        // Assert - the tail reached the DOM even though IsStreaming is still true.
        Assert.Equal("partial answer, tail", JSInterop.Invocations[_jsFunction][^1].Arguments[1]);
    }

    [Fact]
    public void Streaming_Ended_RendersRemainderBelowThreshold()
    {
        // The stream ends with a small tail that never crossed the gate. It must still be
        // rendered - otherwise the last words of every answer would be missing.
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "start")
            .Add(p => p.IsStreaming, true));

        cut.Render(parameters => parameters
            .Add(p => p.Content, "start and a small tail")
            .Add(p => p.IsStreaming, true));

        // Act - streaming finished.
        cut.Render(parameters => parameters
            .Add(p => p.Content, "start and a small tail")
            .Add(p => p.IsStreaming, false));

        // Assert - rendered synchronously on the end of the stream, not left to the timer.
        var invocations = JSInterop.Invocations[_jsFunction];
        Assert.Equal(2, invocations.Count);
        Assert.Equal("start and a small tail", invocations[1].Arguments[1]);
    }

    [Fact]
    public void Streaming_BurstOfTokens_RendersFarLessOftenThanTokens()
    {
        // The actual reason for the gate: 200 tokens/s must not mean 200 renders/s.
        var text = string.Empty;
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "x")
            .Add(p => p.IsStreaming, true));

        // Act - 100 tokens, 10 characters each.
        for (var i = 0; i < 100; i++)
        {
            text += new string('x', 10);
            cut.Render(parameters => parameters
                .Add(p => p.Content, text)
                .Add(p => p.IsStreaming, true));
        }

        // Assert - 1000 chars / 100-char gate = ~10 renders, plus the initial paint. Far fewer
        // than the 100 tokens, which is the point.
        var renders = JSInterop.Invocations[_jsFunction].Count;
        Assert.InRange(renders, 2, 12);

        // The last rendered value must never be behind by more than the gate.
        var lastRendered = (string)JSInterop.Invocations[_jsFunction][^1].Arguments[1];
        Assert.True(text.Length - lastRendered.Length < 100,
            $"last rendered text is {text.Length - lastRendered.Length} chars behind");
    }

    /// <summary>
    /// Polls for a JS render count; <c>WaitForAssertion</c> cannot be used because it only
    /// re-evaluates on a component render, while the flush is a timer-driven JS call.
    /// </summary>
    private async Task WaitForRenderAsync(int expected, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (JSInterop.Invocations[_jsFunction].Count < expected)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"expected {expected} renders, got {JSInterop.Invocations[_jsFunction].Count}");

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ShouldSkipRender_WhenContentUnchanged()
    {
        // Arrange
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "same"));

        // Act - re-render with identical content: ShouldRender returns false,
        // so OnAfterRenderAsync is not executed again
        cut.Render(parameters => parameters
            .Add(p => p.Content, "same"));
        await cut.InvokeAsync(() => { });

        // Assert - still exactly one invocation
        var invocations = JSInterop.VerifyInvoke(_jsFunction, 1);
        Assert.Single(invocations);
    }

    [Fact]
    public void ShouldSwallowJsException()
    {
        // Arrange - simulate disposed element / JS failure via handler exception
        JSInterop.SetupVoid(_jsFunction, _ => true)
            .SetException(new JSException("element is disposed"));

        // Act & Assert - must not throw during render lifecycle
        var exception = Record.Exception(() =>
            Render<MarkdownBlock>(parameters => parameters
                .Add(p => p.Content, "boom")));

        Assert.Null(exception);
    }
}
