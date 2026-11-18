namespace UIBlazor.Tests.Components.ToolViews;

using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

/// <summary>
/// Regression tests for <see cref="DiffViewSection"/>: the section renders the model it is
/// given <b>synchronously</b>.
/// </summary>
/// <remarks>
/// Replaces the former DiffViewSectionThrottleTests. That suite asserted the opposite
/// contract — "after a burst of pushes the final content shows up eventually" — because
/// DiffView/DiffViewSection used to derive from ThrottledComponentBase (500ms window with a
/// deferred trailing render, plus delayed DiffPlex parsing in DiffView). That design lost the
/// final streamed content (todo.md: edit_file rendered only the file name on a fast stream),
/// so the throttling was removed. The regression to guard now is the inverse one: content must
/// be visible immediately after the render, with no waiting.
/// </remarks>
public class DiffViewSectionRenderTests : BunitContext
{
    public DiffViewSectionRenderTests()
    {
        Services.AddRadzenComponents();
    }

    private static SideBySideDiffModel BuildModel(string newText)
    {
        return SideBySideDiffBuilder.Diff(
            Differ.Instance,
            string.Empty,
            newText,
            false,
            false,
            lineChunker: LineChunker.Instance,
            wordChunker: CharacterChunker.Instance);
    }

    [Fact]
    public void Model_IsRenderedSynchronously()
    {
        // Act - no waits, no polling: the very next statement must see the content
        var cut = Render<DiffViewSection>(parameters => parameters
            .Add(p => p.Model, BuildModel("chunk-0")));

        // Assert
        Assert.Contains("chunk-0", cut.Markup);
    }

    [Fact]
    public void RapidModelBurst_FinalContentIsRenderedImmediately()
    {
        // Arrange
        var cut = Render<DiffViewSection>(parameters => parameters
            .Add(p => p.Model, BuildModel("chunk-0")));
        Assert.Contains("chunk-0", cut.Markup);

        // Act - simulate a token burst: a new model on every iteration, no delays at all
        const int lastChunk = 8;
        for (var i = 1; i <= lastChunk; i++)
        {
            var text = $"chunk-{i}";
            cut.Render(parameters => parameters
                .Add(p => p.Model, BuildModel(text)));
        }

        // Assert - the final model is already in the markup (no trailing render needed)
        Assert.Contains($"chunk-{lastChunk}", cut.Markup);
        Assert.DoesNotContain("chunk-0", cut.Markup);
    }
}
