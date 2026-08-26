namespace UIBlazor.Tests.Components.ToolViews;

using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

/// <summary>
/// Tests for <see cref="DiffView"/>, <see cref="DiffViewSection"/> and <see cref="DiffViewLine"/>
/// </summary>
public class DiffViewTests : BunitContext
{
    public DiffViewTests()
    {
        Services.AddRadzenComponents();
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

        // Assert
        Assert.Equal("src\\Program.cs", cut.Find(".tool-file-header").TextContent);
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
    public async Task Streaming_IdenticalReRenderAfterBurst_FinalContentIsDisplayed()
    {
        // Reproduces production bug: while tool-call arguments stream in, each
        // parent push grows Edits and DiffView throttles the renders. When the
        // stream completes, ChatService bumps message state -> parent cascade
        // re-renders with an identical Edits payload -> ParseDiff computes
        // HasChanges=false, and when the pending delayed render fires it was
        // vetoed — the last streamed chunk stayed invisible forever.
        var cut = Render<DiffView>(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, [CreateEdit(null!, "chunk-0")]));
        Assert.Contains("chunk-0", cut.Markup);

        // Stream: append tokens strictly INSIDE one throttle window (all pushes
        // complete within 500ms of the initial render, so none of them can get
        // an immediate leading render — the trailing delayed render is the only
        // chance to display them; payload grows monotonically like SSE appends)
        const string baseChunk = "chunk-0";
        const string finalChunk = baseChunk + "\nstream-token-6-xxxxxxxxxxxxxxxxxxxx" +
                                  "\nfinal-line-marker";
        for (var i = 1; i <= 6; i++)
        {
            await Task.Delay(50);
            var text = i == 6
                ? finalChunk
                : baseChunk + $"\nstream-token-{i}-xxxxxxxxxxxxxxxxxxxx";
            cut.Render(parameters => parameters
                .Add(p => p.FilePath, "src\\Program.cs")
                .Add(p => p.Edits, [CreateEdit(null!, text)]));
        }

        // Stream completed: identical payload pushed again (new array instance,
        // same values) -> ParseDiff sees no growth -> HasChanges=false
        cut.Render(parameters => parameters
            .Add(p => p.FilePath, "src\\Program.cs")
            .Add(p => p.Edits, new[] { CreateEdit(null!, finalChunk) }));

        // Settle past the interval and assert the final line is displayed
        cut.WaitForAssertion(
            () => Assert.Contains("final-line-marker", cut.Markup),
            TimeSpan.FromSeconds(3));
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
}
