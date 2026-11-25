namespace UIBlazor.Tests.Components.ToolViews;

using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Moq;
using UIBlazor.VS;

/// <summary>
/// Tests for <see cref="DiffView"/>, <see cref="DiffViewSection"/> and <see cref="DiffViewLine"/>
/// </summary>
public class DiffViewTests : BunitContext
{
    private readonly Mock<IVsBridge> _vsBridgeMock = new();

    public DiffViewTests()
    {
        Services.AddRadzenComponents();
        Services.AddSingleton(_vsBridgeMock.Object);
        Services.AddSingleton(new UiErrorLogger(new Mock<IJSRuntime>().Object));
    }

    private static DiffEdit CreateEdit(string? oldStr, string newStr, int? approximateLine = null)
    {
        return new DiffEdit { OldStr = oldStr ?? string.Empty, NewStr = newStr, ApproximateLine = approximateLine };
    }

    private static SideBySideDiffModel BuildModel(string oldText, string newText)
    {
        return SideBySideDiffBuilder.Diff(
            Differ.Instance,
            oldText,
            newText,
            false,
            false,
            lineChunker: LineChunker.Instance,
            wordChunker: CharacterChunker.Instance);
    }

    #region DiffView Rendering Tests

    [Fact]
    public void ShouldRenderContainer_WithEmptyEdits()
    {
        // Act
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "file.cs")
            .Add(p => p.Edits, []));

        // Assert
        Assert.NotNull(cut.Find(".diff-view"));
        Assert.NotNull(cut.Find(".diff-block"));
        // No sections rendered
        Assert.Empty(cut.FindAll("table.diff"));
    }

    [Fact]
    public void ShouldRenderFileHeader_WhenFilePathProvided()
    {
        // Act
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, [CreateEdit(null!, "new code")]));

        // Assert - path text lives in the .tool-file-path span (header also holds the open button)
        Assert.Equal("src\\Program.cs", cut.Find(".tool-file-path").TextContent);
    }

    [Fact]
    public void ShouldNotRenderFileHeader_WhenFilePathEmpty()
    {
        // Act
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, string.Empty)
            .Add(p => p.Edits, [CreateEdit(null!, "new code")]));

        // Assert
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-file-header"));
    }

    [Fact]
    public void ShouldRenderOpenFileButton_WhenFilePathProvided()
    {
        // Act
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, [CreateEdit(null!, "new code")]));

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

        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, [CreateEdit(null!, "new code")]));

        // Act
        await cut.InvokeAsync(() => cut.Find(".tool-file-open-btn").Click());

        // Assert - open_file executed with the file path as payload
        _vsBridgeMock.Verify(b => b.ExecuteToolAsync(
            Shared.Contracts.BasicEnum.OpenFile,
            "src\\Program.cs",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpenFileButton_Click_BridgeThrows_DoesNotCrash()
    {
        // Arrange - bridge failure must not crash the UI (and shows the error notification)
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bridge down"));

        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, [CreateEdit(null!, "new code")]));

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
    public async Task OpenFileButton_Click_BridgeFails_ShowsNotification()
    {
        // Arrange - host failure (file not found) must be visible to the user
        _vsBridgeMock
            .Setup(b => b.ExecuteToolAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UIBlazor.Agents.VsToolResult.Failed(
                Shared.Contracts.BasicEnum.OpenFile, "File not found"));

        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, [CreateEdit(null!, "new code")]));

        // Act
        await cut.InvokeAsync(() => cut.Find(".tool-file-open-btn").Click());

        // Assert - error notification shown
        var notification = cut.Services.GetRequiredService<Radzen.NotificationService>();
        Assert.Single(notification.Messages);
        Assert.Equal(Radzen.NotificationSeverity.Error, notification.Messages[0].Severity);
    }

    [Fact]
    public void ShouldRenderSection_WithChangedLines()
    {
        // Arrange
        var edits = new[]
        {
            CreateEdit("aaa\nbbb\nccc", "aaa\nxxx\nccc")
        };

        // Act
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.Edits, edits));

        // Assert - changed lines are typed "modified"; character pieces typed deleted/inserted
        Assert.Single(cut.FindAll("table.diff"));
        Assert.Equal(2, cut.FindAll(".modified-line").Count());
        Assert.NotNull(cut.Find(".deleted-character"));
        Assert.NotNull(cut.Find(".inserted-character"));
        Assert.Contains("bbb", cut.Find(".deleted-character").TextContent);
        Assert.Contains("xxx", cut.Find(".inserted-character").TextContent);
        Assert.Contains("aaa", cut.Markup);
    }

    [Fact]
    public void ShouldRenderSeparator_BetweenMultipleEdits()
    {
        // Arrange
        var edits = new[]
        {
            CreateEdit("old1", "new1"),
            CreateEdit("old2", "new2")
        };

        // Act
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.Edits, edits));

        // Assert - hr between two sections but not before the first
        Assert.Equal(2, cut.FindAll("table.diff").Count());
        Assert.Single(cut.FindAll(".diff-block hr"));
    }

    [Fact]
    public void PositionOffset_ShiftsDisplayedLineNumbers()
    {
        // Arrange - ApproximateLine=11 -> offset 10; first line position 1 -> shows 11
        var edits = new[]
        {
            CreateEdit("line one\nline two", "line one\nline two", 11)
        };

        // Act
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.Edits, edits));

        // Assert
        var numbers = cut.FindAll("td.line-number").Select(td => td.TextContent.Trim()).ToList();
        Assert.Contains("11", numbers);
        Assert.Contains("12", numbers);
    }

    [Fact]
    public async Task ReRender_WithSameEdits_DoesNotDuplicateSections()
    {
        // Arrange
        var edits = new[]
        {
            CreateEdit("a", "b"),
            CreateEdit("c", "d")
        };

        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.Edits, edits));
        Assert.Equal(2, cut.FindAll("table.diff").Count());

        // Act - same content again (processedChars guard prevents re-parse)
        cut.Render(parameters => parameters
            .Add(p => p.Edits, edits));
        await cut.InvokeAsync(() => { });

        // Assert
        Assert.Equal(2, cut.FindAll("table.diff").Count());
    }

    #endregion

    #region Streaming Regression Tests

    [Fact]
    public void Streaming_IdenticalReRenderAfterBurst_FinalContentIsDisplayed()
    {
        // Reproduces production bug (todo.md): while tool-call arguments stream in, every
        // parent push grew Edits. DiffView used to throttle its renders (500ms, deferred
        // DiffPlex parsing) — on a fast stream the last chunk landed inside the window, the
        // trailing render was vetoed by HasChanges and the content stayed invisible forever.
        // DiffView now parses synchronously in OnParametersSet: whatever arrives is parsed
        // before the closest render, so the assertion below needs no waiting.
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, [CreateEdit(null!, "chunk-0")]));
        Assert.Contains("chunk-0", cut.Markup);

        // Stream: append tokens back-to-back (payload grows monotonically like SSE appends)
        const string baseChunk = "chunk-0";
        const string finalChunk = baseChunk + "\nstream-token-6-xxxxxxxxxxxxxxxxxxxx" +
                                  "\nfinal-line-marker";
        for (var i = 1; i <= 6; i++)
        {
            var text = i == 6
                ? finalChunk
                : baseChunk + $"\nstream-token-{i}-xxxxxxxxxxxxxxxxxxxx";
            cut.Render(parameters => parameters
                .Add(p => p.FilePath, "src\\Program.cs")
                .Add(p => p.Edits, [CreateEdit(null!, text)]));

            // Synchronous contract: the payload just pushed is ALREADY parsed — the row
            // count matches the latest text (CharacterChunker breaks the changed token into
            // per-character spans, so text markers are not reliable here).
            var expectedRows = i == 6 ? 3 : 2; // chunk-0 / stream-token-i / final-line-marker
            Assert.Equal(expectedRows, cut.FindAll("table.diff tr").Count());
        }

        // Stream completed: identical payload pushed again (new array instance, same values)
        cut.Render(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, [CreateEdit(null!, finalChunk)]));

        // Assert - the final line survived the identical re-render cascade
        Assert.Contains("final-line-marker", cut.Markup);
    }

    [Fact]
    public void FilePathChange_WithIdenticalEdits_IsRendered()
    {
        // Reproduces production bug: FilePath does NOT contribute to the sum-of-chars
        // change detection. When the final update brings the file path while Edits stay
        // identical (the path arrives as the last chunk of streaming args), the change
        // detection must still see "nothing to re-parse" AND the path must be rendered.
        var edits = new[]
        {
            CreateEdit("old", "new")
        };

        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, string.Empty)
            .Add(p => p.Edits, edits));
        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-file-header"));

        // Final update: identical Edits payload (new array instance, same values) + FilePath.
        // The header is rendered from the parameter directly — no diffuse dependency.
        cut.Render(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, edits));

        Assert.Equal("src\\Program.cs", cut.Find(".tool-file-path").TextContent);
    }

    [Fact]
    public void FilePathChange_WithGrownEdits_RendersBoth()
    {
        // FilePath changes AND Edits grow in the same update: both the header and the
        // new diff content must be rendered by that very render.
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Edits, [CreateEdit("old", "new")]));

        cut.Render(parameters => parameters
            .Add(p => p.FilePath, "b.cs")
            .Add(p => p.Edits, [CreateEdit("old", "new"), CreateEdit("x", "y")]));

        Assert.Equal("b.cs", cut.Find(".tool-file-path").TextContent);
        Assert.Equal(2, cut.FindAll("table.diff").Count());
    }

    #endregion

    #region DiffViewSection Tests

    [Fact]
    public void Section_ShouldRenderRowPerLinePair()
    {
        // Arrange
        var model = BuildModel("one\ntwo", "one\ntwo");

        // Act
        var cut = Render<DiffViewSection>(parameters => parameters
            .Add(p => p.Model, model));

        // Assert - two aligned rows, each with both sides visible
        var rows = cut.FindAll("tr");
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.Equal(4, row.Children.Length); // old number + old line + new number + new line
        }
    }

    [Fact]
    public void Section_PositionOffset_IsAddedToLineNumbers()
    {
        // Arrange
        var model = BuildModel("single", "single");

        // Act
        var cut = Render<DiffViewSection>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.PositionOffset, 100));

        // Assert
        var numbers = cut.FindAll("td.line-number").Select(td => td.TextContent.Trim()).ToList();
        Assert.Contains("101", numbers);
    }

    [Fact]
    public async Task Section_ClickLineNumber_HidesOldPane()
    {
        // Arrange - two lines so the table has two rows
        var model = BuildModel("keep\ntoo", "keep\ntoo");
        var cut = Render<DiffViewSection>(parameters => parameters
            .Add(p => p.Model, model));

        Assert.Equal(2, cut.FindAll("tr").Count);

        // Act
        await cut.InvokeAsync(() => cut.Find("td.line-number").Click());

        // Assert - only new side remains, stretched to full width
        var rows = cut.FindAll("tr");
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.Equal(2, row.Children.Length); // new number + new line
        }

        var newLineCell = cut.Find("td.line.unchanged-line");
        Assert.Equal("width: 100%", newLineCell.GetAttribute("style"));
    }

    #endregion

    #region DiffViewLine Tests

    [Fact]
    public void Line_Unchanged_RendersRawTextWithoutSpans()
    {
        // Arrange
        var model = BuildModel("plain", "plain");
        var unchangedPiece = model.OldText.Lines[0];

        // Act
        var cut = Render<DiffViewLine>(parameters => parameters
            .Add(p => p.Model, unchangedPiece!));

        // Assert
        Assert.Equal("plain", cut.Markup.Trim());
        Assert.DoesNotContain("<span", cut.Markup);
    }

    [Fact]
    public void Line_EmptyText_RendersNothing()
    {
        // Act
        var cut = Render<DiffViewLine>(parameters => parameters
            .Add(p => p.Model, new DiffPiece(string.Empty, ChangeType.Unchanged)));

        // Assert
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void Line_FullyChangedWord_RendersTypedSpan()
    {
        // Arrange - "one two" -> "one THREE": tail words are fully inserted/deleted.
        // Fully changed words render with the {type}-character css class.
        var model = BuildModel("one two", "one THREE");
        var deletedPiece = model.OldText.Lines[0]!;
        var insertedPiece = model.NewText.Lines[0]!;

        // Act - old side of the changed line
        var cutOld = Render<DiffViewLine>(parameters => parameters
            .Add(p => p.Model, deletedPiece));

        // Assert - fully deleted word wrapped in typed span
        var spanOld = cutOld.Find("span.deleted-character");
        Assert.Equal("two", spanOld.TextContent);
        Assert.Contains("one", cutOld.Markup);

        // New side
        var cutNew = Render<DiffViewLine>(parameters => parameters
            .Add(p => p.Model, insertedPiece));

        var spanNew = cutNew.Find("span.inserted-character");
        Assert.Equal("THREE", spanNew.TextContent);
    }

    [Fact]
    public void Line_PartiallyChangedWord_WrapsOnlyChangedCharacters()
    {
        // Arrange - "abcd" -> "abXd": single mixed word, only "c"/"X" are typed.
        // The mixed word wrapper gets the {dominant type}-word css class.
        var model = BuildModel("abcd", "abXd");
        var piece = model.OldText.Lines[0]!;

        // Act
        var cut = Render<DiffViewLine>(parameters => parameters
            .Add(p => p.Model, piece));

        // Assert - outer word span + inner character span for the changed char
        var wordSpan = cut.Find("span.deleted-word");
        Assert.NotNull(wordSpan);
        Assert.Equal("abcd", wordSpan.TextContent);

        var charSpan = cut.Find("span.deleted-character");
        Assert.Equal("c", charSpan.TextContent);
    }

    #endregion

    #region Call-argument badges

    [Fact]
    public void ReplaceAllBadge_IsAbsent_WhenNoEditReplacesAll()
    {
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Edits, new[] { CreateEdit("a", "b") }));

        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-replaceall-badge"));
    }

    [Fact]
    public void ReplaceAllBadge_CountsOnlyTheEditsThatCarryTheFlag()
    {
        // A batch may mix both kinds, so the badge is a COUNT: a plain flag would hide which
        // edit replaces everything and would read as "the whole batch replaces all".
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Edits, new[]
            {
                new DiffEdit { OldStr = "a", NewStr = "b", ReplaceAll = true },
                CreateEdit("c", "d"),
                new DiffEdit { OldStr = "e", NewStr = "f", ReplaceAll = true }
            }));

        // The denominator is the point: `×2` alone read as "2 replacements" next to a result that
        // said "4 replacements". The badge counts EDITS, so it must say so.
        Assert.Contains("2/3", cut.Find(".tool-replaceall-badge").TextContent);
    }

    [Fact]
    public void ReplaceAllBadge_ShowsTheEditRatioNotAReplacementCount()
    {
        // The exact confusion the `×N` label caused: a call whose FIRST edit replaces all and whose
        // second edit is a plain one reported `×1`, while the tool result read "1/2 edits,
        // 4 replacements". The occurrence count is a property of the FILE and is unknown until the
        // host runs the call, so the badge cannot show it — it must not LOOK like it does.
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Edits, new[]
            {
                new DiffEdit { OldStr = "a", NewStr = "A", ReplaceAll = true },
                CreateEdit("TARGET", "DONE")
            }));

        var text = cut.Find(".tool-replaceall-badge").TextContent;
        Assert.Contains("1/2", text);
        Assert.DoesNotContain("×", text);
        Assert.Contains("edits", text);
    }

    [Fact]
    public void ReplaceAllBadge_TreatsNullAsFalse()
    {
        // DiffEdit.ReplaceAll is a bool? because the schema allows `"replaceAll": null`.
        // Null means false, so it must not be counted.
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Edits, new[] { new DiffEdit { OldStr = "a", NewStr = "b", ReplaceAll = null } }));

        Assert.Throws<ElementNotFoundException>(() => cut.Find(".tool-replaceall-badge"));
    }

    [Fact]
    public void DryRunBadge_IsShownOnlyWhenTheCallIsADryRun()
    {
        // A successful-looking diff is otherwise indistinguishable from a real write.
        var dry = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Edits, new[] { CreateEdit("a", "b") })
            .Add(p => p.DryRun, true));

        // Warning, not Info: the badge says NOTHING was written, and "Info" reads as a neutral
        // remark about a completed action. Pinned so the style cannot silently regress.
        Assert.Contains("rz-badge-warning", dry.Find(".tool-dryrun-badge").ClassList);

        var real = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "a.cs")
            .Add(p => p.Edits, new[] { CreateEdit("a", "b") }));

        Assert.Throws<ElementNotFoundException>(() => real.Find(".tool-dryrun-badge"));
    }

    [Fact]
    public void Badges_AreNotRendered_WithoutFilePath()
    {
        // Both badges live in the file header, which only exists when a path is present.
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, string.Empty)
            .Add(p => p.Edits, new[] { new DiffEdit { OldStr = "a", NewStr = "b", ReplaceAll = true } })
            .Add(p => p.DryRun, true));

        Assert.Empty(cut.FindAll(".tool-replaceall-badge"));
        Assert.Empty(cut.FindAll(".tool-dryrun-badge"));
    }

    [Fact]
    public void NullOldStrAndNewStr_StillRenderTheHeaderAndTheBlock()
    {
        // `DiffEdit.OldStr`/`NewStr` are non-nullable with `string.Empty` initialisers, but STJ sets
        // them to null when the payload says `"oldStr": null`. Reading `.Length` on them threw inside
        // OnParametersSet, and the user saw NEITHER the diff, nor the path, nor a reason — only an
        // empty box above the approval buttons.
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "src/App.cs")
            .Add(p => p.Edits, new[] { new DiffEdit { OldStr = null!, NewStr = null! } }));

        Assert.Contains("src/App.cs", cut.Markup);
        // The diff BODY has to exist too: the defect blanked the whole block, not just the header.
        Assert.Single(cut.FindAll(".diff-pane"));
    }

    #endregion
}
