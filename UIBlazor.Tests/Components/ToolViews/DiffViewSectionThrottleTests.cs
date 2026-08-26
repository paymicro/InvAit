namespace UIBlazor.Tests.Components.ToolViews;

using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

/// <summary>
/// Streaming regression tests for <see cref="DiffViewSection"/> render throttling:
/// rapid model updates (token bursts) must still display the final content.
/// </summary>
public class DiffViewSectionThrottleTests : BunitContext
{
    public DiffViewSectionThrottleTests()
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
    public async Task Streaming_BurstThenSettle_FinalLineIsDisplayed()
    {
        // Arrange - initial render with first chunk
        var cut = Render<DiffViewSection>(parameters => parameters
            .Add(p => p.Model, BuildModel("chunk-0")));
        Assert.Contains("chunk-0", cut.Markup);

        // Act - simulate streaming: new model every 100ms (inside the 500ms window)
        const int lastChunk = 8;
        for (var i = 1; i <= lastChunk; i++)
        {
            await Task.Delay(100);
            var text = $"chunk-{i}";
            cut.Render(parameters => parameters
                .Add(p => p.Model, BuildModel(text)));
        }

        // Settle - wait past the throttle interval for the trailing render
        await Task.Delay(800);

        // Assert - the final streamed line MUST be visible
        cut.WaitForAssertion(
            () => Assert.Contains($"chunk-{lastChunk}", cut.Markup),
            TimeSpan.FromSeconds(3));
    }
}
