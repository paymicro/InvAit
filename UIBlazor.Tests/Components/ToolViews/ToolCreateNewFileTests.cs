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
    public void ShouldRenderContent_InPreElement()
    {
        // Arrange
        var content = "namespace Demo;\n\npublic class Demo { }";

        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.Content, content));

        // Assert
        var pre = cut.Find(".diff-block pre");
        Assert.Contains(content, pre.TextContent);
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
    public void ShouldUpdateContent_WhenParameterChanges()
    {
        // Arrange
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.Content, "old content"));

        // Act
        cut.Render(parameters => parameters
            .Add(p => p.Content, "new content"));

        // Assert
        Assert.Contains("new content", cut.Find(".diff-block pre").TextContent);
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
    public void ShouldInvokeHighlightJs_WithMappedLanguage()
    {
        // Act
        var cut = Render<ToolCreateNewFile>(parameters => parameters
            .Add(p => p.FilePath, "query.sql")
            .Add(p => p.Content, "SELECT 1"));

        // Assert - highlightCodeElement invoked with the mapped language
        cut.WaitForAssertion(() =>
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

        cut.WaitForAssertion(() =>
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

        cut.WaitForAssertion(() =>
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

    #endregion
}
