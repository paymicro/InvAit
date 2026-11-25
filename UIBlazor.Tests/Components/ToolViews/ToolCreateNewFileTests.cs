namespace UIBlazor.Tests.Components.ToolViews;

using Moq;
using UIBlazor.VS;

/// <summary>
/// Tests for <see cref="ToolCreateNewFile"/>
/// </summary>
public class ToolCreateNewFileTests : BunitContext
{
    private readonly Mock<IVsBridge> _vsBridgeMock = new();

    public ToolCreateNewFileTests()
    {
        Services.AddRadzenComponents();
        Services.AddSingleton(_vsBridgeMock.Object);
        Services.AddSingleton(new UiErrorLogger(new Mock<IJSRuntime>().Object));
        JSInterop.SetupVoid("highlightCodeElement", _ => true);
    }

    [Fact]
    public void ShouldRenderDiffViewContainer()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "C:\\src\\Program.cs")
            .Add(p => p.Content, "Console.WriteLine();"));

        // Assert
        Assert.NotNull(cut.Find(".diff-view"));
        Assert.NotNull(cut.Find(".diff-block"));
    }

    [Fact]
    public void ShouldRenderFilePathHeader()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "C:\\src\\MyService.cs")
            .Add(p => p.Content, "code"));

        // Assert - path text lives in the .tool-file-path span (header also holds the open button)
        Assert.Equal("C:\\src\\MyService.cs", cut.Find(".tool-file-path").TextContent);
    }

    [Fact]
    public void ShouldNotRenderFilePathHeader_WhenFilePathIsEmpty()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, string.Empty)
            .Add(p => p.Content, "code"));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-file-header"));
    }

    [Fact]
    public async Task ShouldPassContent_ToHighlightJs()
    {
        // Arrange - MarkdownBlock pattern: the code element stays empty in the DOM,
        // content is delivered to the highlighter via JS interop (third argument).
        var content = "namespace Demo;\n\npublic class Demo { }";

        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.Content, content));

        // Assert
        await cut.WaitForAssertionAsync(() =>
        {
            var invocation = JSInterop.VerifyInvoke("highlightCodeElement");
            Assert.Equal(content, invocation.Arguments[2].ToString());
        });
    }

    [Fact]
    public void ShouldRenderEmptyPre_WhenContentNotProvided()
    {
        // Act
        var cut = Render<ToolCreateNewFile>();

        // Assert - renders without error
        Assert.NotNull(cut.Find(".diff-block pre"));
    }

    [Fact]
    public async Task ShouldUpdateContent_WhenParameterChanges()
    {
        // Arrange
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.Content, "old content"));

        await cut.WaitForAssertionAsync(() => JSInterop.VerifyInvoke("highlightCodeElement", 1));

        // Act
        cut.Render(parameters => parameters
            .Add(p => p.Content, "new content"));
        await cut.InvokeAsync(() => { });

        // Assert - second invocation carries the new content
        var invocation = JSInterop.VerifyInvoke("highlightCodeElement", 2);
        Assert.Equal("new content", invocation[1].Arguments[2].ToString());
    }

    #region Syntax Highlighting Tests

    [Fact]
    public void ShouldRenderOpenFileButton_WhenFilePathProvided()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Content, "class Demo { }"));

        // Assert
        Assert.NotNull(cut.Find(".tool-file-open-btn"));
    }

    [Fact]
    public async Task OpenFileButton_Click_ExecutesOpenFileTool()
    {
        // Arrange - success result: no notification
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UIBlazor.Agents.VsToolResult { Success = true });

        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Content, "class Demo { }"));

        // Act
        await cut.InvokeAsync(() => cut.Find(".tool-file-open-btn").Click());

        // Assert - open_file executed with the file path as payload, no notification on success
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(
            Shared.Contracts.BasicEnum.OpenFile,
            "src\\Program.cs",
            It.IsAny<CancellationToken>()), Times.Once);
        var notification = cut.Services.GetRequiredService<Radzen.NotificationService>();
        Assert.Empty(notification.Messages);
    }

    [Fact]
    public async Task OpenFileButton_Click_BridgeThrows_DoesNotCrash()
    {
        // Arrange - bridge failure must not crash the UI (and shows the error notification)
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bridge down"));

        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Content, "class Demo { }"));

        // Act + Assert - no exception escapes the click handler
        await cut.InvokeAsync(() => cut.Find(".tool-file-open-btn").Click());
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        var notification = cut.Services.GetRequiredService<Radzen.NotificationService>();
        Assert.Single(notification.Messages);
    }

    [Fact]
    public void ShouldRenderHljsClass_ForKnownExtension()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Content, "class Demo { }"));

        // Assert - .cs maps to the csharp Highlight.js language
        var code = cut.Find(".diff-block code");
        Assert.Contains("hljs", code.GetAttribute("class"));
        Assert.Contains("language-csharp", code.GetAttribute("class"));
    }

    [Fact]
    public void ShouldRenderPlainHljsClass_ForUnknownExtension()
    {
        // Act - unknown extension -> no language class (JS falls back to plaintext)
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "data.unknownext")
            .Add(p => p.Content, "raw"));

        // Assert
        var code = cut.Find(".diff-block code");
        Assert.Contains("hljs", code.GetAttribute("class"));
        Assert.DoesNotContain("language-", code.GetAttribute("class"));
    }

    [Fact]
    public async Task ShouldInvokeHighlightJs_WithMappedLanguage()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "query.sql")
            .Add(p => p.Content, "SELECT 1"));

        // Assert - highlightCodeElement invoked with the mapped language
        await cut.WaitForAssertionAsync(() =>
        {
            var invocation = JSInterop.VerifyInvoke("highlightCodeElement");
            Assert.Contains("sql", invocation.Arguments[1].ToString());
        });
    }

    [Fact]
    public async Task OpenFileButton_Click_BridgeFails_ShowsNotification()
    {
        // Arrange - host failure (file not found) must be visible to the user
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UIBlazor.Agents.VsToolResult.Failed(
                Shared.Contracts.BasicEnum.OpenFile, "File not found"));

        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Content, "class Demo { }"));

        // Act
        await cut.InvokeAsync(() => cut.Find(".tool-file-open-btn").Click());

        // Assert - error notification shown
        var notification = cut.Services.GetRequiredService<Radzen.NotificationService>();
        Assert.Single(notification.Messages);
        Assert.Equal(Radzen.NotificationSeverity.Error, notification.Messages[0].Severity);
    }

    [Fact]
    public async Task ShouldNotReinvokeHighlight_WhenContentUnchanged()
    {
        // Arrange
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code"));

        await cut.WaitForAssertionAsync(() =>
            JSInterop.VerifyInvoke("highlightCodeElement", 1));

        // Act - identical re-render (same content + same language)
        cut.Render(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code"));
        await cut.InvokeAsync(() => { });

        // Assert - no additional highlight invocation
        var invocation = JSInterop.VerifyInvoke("highlightCodeElement", 1);
        Assert.Equal("csharp", invocation[0].Arguments[1].ToString());
    }

    [Fact]
    public async Task ShouldReinvokeHighlight_WhenContentChanges()
    {
        // Arrange
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "old"));

        await cut.WaitForAssertionAsync(() =>
            JSInterop.VerifyInvoke("highlightCodeElement", 1));

        // Act - content changes -> re-highlight
        cut.Render(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "new content"));
        await cut.InvokeAsync(() => { });

        // Assert
        var invocation = JSInterop.VerifyInvoke("highlightCodeElement", 2);
        Assert.Equal("csharp", invocation[1].Arguments[1].ToString());
    }

    [Fact]
    public async Task ShouldReinvokeHighlight_WhenContentGrowsFromEmpty()
    {
        // Regression: during streaming the first render has empty content. The
        // highlighter must be re-invoked when the content arrives (was stuck
        // before because Blazor text node was destroyed by innerHTML).
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs"));

        await cut.WaitForAssertionAsync(() =>
            JSInterop.VerifyInvoke("highlightCodeElement", 1));

        // Act - content arrives after the empty first render
        cut.Render(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "arrived"));
        await cut.InvokeAsync(() => { });

        // Assert - second invocation carries the arrived content
        var invocation = JSInterop.VerifyInvoke("highlightCodeElement", 2);
        Assert.Equal("arrived", invocation[1].Arguments[2].ToString());
    }

    #endregion

    #region Mode badge

    [Fact]
    public void ModeBadge_IsShownForOmittedMode_AsOverwrite()
    {
        // Null/empty means overwrite in the host, so the block must say so instead of showing
        // nothing: a create_file block with no badge reads as "mode unknown".
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code"));

        var badge = cut.Find(".tool-mode-badge");
        Assert.Contains(SharedResource.FileModeOverwrite, badge.TextContent);
    }

    [Theory]
    [InlineData("append", "FileModeAppend")]
    [InlineData("APPEND", "FileModeAppend")]
    [InlineData(" append ", "FileModeAppend")]
    [InlineData("prepend", "FileModePrepend")]
    [InlineData("overwrite", "FileModeOverwrite")]
    [InlineData("create", "FileModeOverwrite")]
    [InlineData("replace", "FileModeOverwrite")]
    public void ModeBadge_ResolvesEveryAliasTheHostAccepts(string mode, string expectedKey)
    {
        // The badge must resolve the SAME alias table the host uses (FileWriteModeNames):
        // `create` and `replace` are overwrite, and a second copy of that table in the UI would
        // eventually label a call with a mode the host never applied.
        var expected = expectedKey switch
        {
            "FileModeAppend" => SharedResource.FileModeAppend,
            "FileModePrepend" => SharedResource.FileModePrepend,
            _ => SharedResource.FileModeOverwrite
        };

        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code")
            .Add(p => p.Mode, mode));

        Assert.Contains(expected, cut.Find(".tool-mode-badge").TextContent);
    }

    [Fact]
    public void ModeBadge_UnknownMode_ShowsRawValueAndIsNotLabelledOverwrite()
    {
        // The host REFUSES an unknown mode, so labelling it "overwrite" would be a lie about
        // what happened. The raw value is what the model needs to see to fix its own call.
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code")
            .Add(p => p.Mode, "sideways"));

        var badge = cut.Find(".tool-mode-badge");
        Assert.Contains("sideways", badge.TextContent);
        Assert.DoesNotContain(SharedResource.FileModeOverwrite, badge.TextContent);
    }

    [Fact]
    public void ModeBadge_IsNeutral_WhenModeHasNotArrivedYet()
    {
        // During streaming `mode` may be absent. It resolves to overwrite, but warning about a
        // destructive mode the call never asked for would be a lie about the request.
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code")
            .Add(p => p.ModeReceived, false)
            .Add(p => p.ArgumentsFinal, false));

        var badge = cut.Find(".tool-mode-badge");
        Assert.Contains("tool-mode-pending", badge.ClassList);
        Assert.DoesNotContain(SharedResource.FileModeOverwrite, badge.TextContent);
    }

    [Fact]
    public void ModeBadge_ShowsOverwrite_WhenTheCallCompletedWithoutAMode()
    {
        // An absent `mode` on a FINISHED call is not "still loading": the host applied overwrite,
        // so the block must say so. Leaving the ellipsis made a completed call look permanently
        // pending — a session saved before the argument existed kept it forever.
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code")
            .Add(p => p.ModeReceived, false)
            .Add(p => p.ArgumentsFinal, true));

        var badge = cut.Find(".tool-mode-badge");
        Assert.Contains(SharedResource.FileModeOverwrite, badge.TextContent);
        Assert.DoesNotContain("tool-mode-pending", badge.ClassList);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("pre")]
    [InlineData("overw")]
    [InlineData("creat")]
    [InlineData("")]
    public void ModeBadge_IsNeutral_ForAValueThatIsStillArriving(string partial)
    {
        // The arguments are repaired to valid JSON at EVERY chunk, so `"mode": "append"` arrives
        // mid-stream as the perfectly legal `"app"`. A Danger badge there claims the host refuses a
        // call that was never broken — the exact "badge flashes a wrong verdict" it was built to avoid.
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code")
            .Add(p => p.Mode, partial)
            .Add(p => p.ArgumentsFinal, false));

        Assert.Contains("tool-mode-pending", cut.Find(".tool-mode-badge").ClassList);
    }

    [Fact]
    public void ModeBadge_PartialValueOnAFinishedCall_IsTheRefusalTheHostGives()
    {
        // The same `app` once the arguments are final is what the model actually sent, and the host
        // refuses it — so the badge must not keep pretending it is still loading.
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code")
            .Add(p => p.Mode, "app")
            .Add(p => p.ArgumentsFinal, true));

        var badge = cut.Find(".tool-mode-badge");
        Assert.Contains("app", badge.TextContent);
        Assert.Contains("rz-badge-danger", badge.ClassList);
        Assert.DoesNotContain("tool-mode-pending", badge.ClassList);
    }

    [Fact]
    public void ModeBadge_AppendIsSuccessAndOverwriteIsWarning()
    {
        // Overwrite is the destructive mode (it deletes the old bytes), append/prepend keep them.
        var append = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code")
            .Add(p => p.Mode, "append"));
        var overwrite = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Content, "code")
            .Add(p => p.Mode, "overwrite"));

        Assert.Contains("rz-badge-success", append.Find(".tool-mode-badge").ClassList);
        Assert.Contains("rz-badge-warning", overwrite.Find(".tool-mode-badge").ClassList);
    }

    #endregion
}
