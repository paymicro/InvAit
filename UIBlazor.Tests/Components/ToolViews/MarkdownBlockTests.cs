namespace UIBlazor.Tests.Components.ToolViews;

/// <summary>
/// Tests for <see cref="MarkdownBlock"/>
/// </summary>
public class MarkdownBlockTests : BunitContext
{
    private const string JsFunction = "renderMarkdownToElement";

    public MarkdownBlockTests()
    {
        JSInterop.SetupVoid(JsFunction, _ => true);
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
        var invocation = JSInterop.VerifyInvoke(JsFunction);
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
        Assert.Throws<JSInvokeCountExpectedException>(() => JSInterop.VerifyInvoke(JsFunction));
    }

    [Fact]
    public async Task ShouldCallJsAgain_WhenContentChanges()
    {
        // Arrange
        var cut = Render<MarkdownBlock>(parameters => parameters
            .Add(p => p.Content, "first"));

        // Act
        cut.Render(parameters => parameters
            .Add(p => p.Content, "second"));
        await cut.InvokeAsync(() => { });

        // Assert - VerifyInvoke(name, 2) checks total count and returns all invocations
        var invocations = JSInterop.VerifyInvoke(JsFunction, 2);
        Assert.Equal(2, invocations.Count);
        Assert.Equal("second", invocations[1].Arguments[1]);
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
        var invocations = JSInterop.VerifyInvoke(JsFunction, 1);
        Assert.Single(invocations);
    }

    [Fact]
    public void ShouldSwallowJsException()
    {
        // Arrange - simulate disposed element / JS failure via handler exception
        JSInterop.SetupVoid(JsFunction, _ => true)
            .SetException(new JSException("element is disposed"));

        // Act & Assert - must not throw during render lifecycle
        var exception = Record.Exception(() =>
            Render<MarkdownBlock>(parameters => parameters
                .Add(p => p.Content, "boom")));

        Assert.Null(exception);
    }
}
