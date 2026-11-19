namespace UIBlazor.Tests.Services;

/// <summary>
/// Inline think-block handling (Kimi / DeepSeek R1 style), where the reasoning arrives inside
/// <c>Content</c> instead of a dedicated reasoning channel.
/// <para>
/// The helper is a pure function over one delta plus two carried flags, so the interesting cases are
/// all boundaries: the opening tag, the closing tag, and a chunk that carries BOTH halves. The three
/// defects pinned here all corrupt user-visible text, which is why they survived unnoticed — a
/// mangled <c>&lt;think&gt;</c> inside a code fence still renders, just wrongly.
/// </para>
/// </summary>
public class AfThinkBlockTests
{
    /// <summary>Carried flags of one stream, exactly as <see cref="AfDeltaMapper"/> holds them.</summary>
    private sealed class Stream
    {
        public bool IsStart = true;
        public bool InThink;
    }

    private static string? Feed(Stream stream, string chunk)
    {
        var result = AfUsageHelpers.ProcessThinkBlocks(chunk, ref stream.IsStart, ref stream.InThink);

        // The mapper merges the two halves into one delta; a test asserting on one half must know
        // the other was empty, otherwise a chunk could silently be both reasoning and answer.
        return result.Reasoning is not null && result.Content is not null
            ? $"{result.Reasoning}|{result.Content}"
            : result.Reasoning ?? result.Content;
    }

    [Fact]
    public void PlainChunk_GoesToContent()
    {
        var stream = new Stream();

        Assert.Equal("answer", Feed(stream, "answer"));
        Assert.False(stream.InThink);
    }

    [Fact]
    public void FirstChunkOpeningTag_IsReasoning_WithoutTheTag()
    {
        var stream = new Stream();

        Assert.Equal("thinking", Feed(stream, "<think>thinking"));
        Assert.True(stream.InThink);
    }

    /// <summary>
    /// A chunk that documents the tag must keep it. <c>Replace</c> removed EVERY occurrence, so the
    /// text the model wrote about the tag disappeared from the transcript.
    /// </summary>
    [Fact]
    public void SecondOpenTagInSameChunk_IsPreservedAsText()
    {
        var stream = new Stream();

        Assert.Equal("a <think> b", Feed(stream, "<think>a <think> b"));
    }

    /// <summary>
    /// The closing chunk is TWO payloads: reasoning before the tag, answer after it. The old code
    /// put the whole chunk into Content — the model's thoughts leaked into the visible answer.
    /// </summary>
    [Fact]
    public void ClosingChunk_SplitsReasoningFromAnswer()
    {
        var stream = new Stream();
        Feed(stream, "<think>still thinking");

        Assert.Equal("done thinking|the answer", Feed(stream, "done thinking</think>the answer"));
        Assert.False(stream.InThink);
    }

    [Fact]
    public void ClosingChunk_WithNothingBeforeTheTag_IsAnswerOnly()
    {
        var stream = new Stream();
        Feed(stream, "<think>reasoning");

        Assert.Equal("answer", Feed(stream, "</think>answer"));
    }

    [Fact]
    public void ClosingChunk_WithNothingAfterTheTag_IsReasoningOnly()
    {
        var stream = new Stream();
        Feed(stream, "<think>reasoning");

        Assert.Equal("more reasoning", Feed(stream, "more reasoning</think>"));
        Assert.False(stream.InThink);
    }

    /// <summary>
    /// Only the FIRST closer is consumed; a second one in the same chunk belongs to the answer.
    /// <c>Replace</c> stripped every closer, so an answer that quoted the tag lost characters.
    /// <para>
    /// A closer inside a code fence cannot be told apart from a real one while streaming: the block
    /// ends at the first occurrence by contract. That limit is inherent to reading a tag out of a
    /// token stream, not something a smarter slice could fix.
    /// </para>
    /// </summary>
    [Fact]
    public void SecondCloserInSameChunk_StaysInTheAnswer()
    {
        var stream = new Stream();
        Feed(stream, "<think>reasoning");

        Assert.Equal("done|explaining </think> in prose", Feed(stream, "done</think>explaining </think> in prose"));
    }

    /// <summary>
    /// The opening tag may only open a block at the START of the turn. The old code never cleared
    /// <c>isStart</c>, so a mid-answer chunk that began with the literal tag was swallowed as
    /// reasoning.
    /// </summary>
    [Fact]
    public void OpenTagInLaterChunk_IsContent()
    {
        var stream = new Stream();
        Feed(stream, "the answer mentions");

        Assert.Equal("<think> as a tag", Feed(stream, "<think> as a tag"));
        Assert.False(stream.InThink);
    }

    /// <summary>Empty deltas (usage-only chunks) must not spend the opening-tag window.</summary>
    [Fact]
    public void EmptyChunk_KeepsTheOpeningWindow()
    {
        var stream = new Stream();

        Assert.Null(Feed(stream, string.Empty));
        Assert.True(stream.IsStart);

        Assert.Equal("thinking", Feed(stream, "<think>thinking"));
        Assert.True(stream.InThink);
    }
}
