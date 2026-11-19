namespace UIBlazor.Tests.Services;

/// <summary>
/// <see cref="MessageTimingsCalculator"/> — the pure function behind the message badges.
/// <para>
/// These numbers are the user's only evidence of how fast the model actually is, so every case here
/// is a sentence from the badge's contract: what the latency is, what the generation speed covers,
/// and what happens when the time to first token dominates.
/// </para>
/// </summary>
public class MessageTimingsCalculatorTests
{
    private static CompletionsResult Capture(int completionTokens, int reasoningTokens = 0)
        => new() { CompletionTokens = completionTokens, ReasoningTokens = reasoningTokens };

    [Fact]
    public void Speed_ExcludesTimeToFirstToken()
    {
        // 1000 tokens, 1 s wait, 4 s of generation → 250 t/s.
        // Counting the wait would report 200 t/s: a fast model punished for the provider's TTFT.
        var timings = new MessageTimings();

        MessageTimingsCalculator.Apply(timings, Capture(1000), elapsedMs: 5000, firstTokenMs: 1000);

        Assert.Equal(250f, timings.TokensInSec, 0.01f);
    }

    [Fact]
    public void Total_And_FirstToken_AreReportedSeparately()
    {
        var timings = new MessageTimings();

        MessageTimingsCalculator.Apply(timings, Capture(10), elapsedMs: 3000, firstTokenMs: 1200);

        Assert.Equal(TimeSpan.FromMilliseconds(3000), timings.Total);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), timings.FirstToken);
    }

    /// <summary>
    /// Reasoning is generated output: its tokens are in the numerator AND the time it took is in the
    /// window (the window starts at the first token of any kind). A reasoning model therefore reports
    /// the speed it actually produced, not a number that belongs to no real process.
    /// </summary>
    [Fact]
    public void Reasoning_IsCountedInBothTheTokensAndTheWindow()
    {
        // 4000 reasoning + 1000 answer tokens; first token at 0.5 s, done at 10.5 s → 500 t/s.
        var timings = new MessageTimings();

        MessageTimingsCalculator.Apply(timings, Capture(5000, reasoningTokens: 4000),
            elapsedMs: 10_500, firstTokenMs: 500);

        Assert.Equal(5000, timings.Tokens);
        Assert.Equal(4000, timings.ReasoningTokens);
        Assert.Equal(500f, timings.TokensInSec, 0.01f);
    }

    /// <summary>
    /// A one-second floor used to report every sub-second answer at "tokens / 1 s", i.e. exactly the
    /// token count — the badge read 200 t/s for 200 tokens generated in 0.4 s.
    /// </summary>
    [Fact]
    public void FastAnswer_IsNotFlooredToOneSecond()
    {
        var timings = new MessageTimings();

        // 200 tokens, first token at 100 ms, finished at 500 ms → 0.4 s window → 500 t/s.
        MessageTimingsCalculator.Apply(timings, Capture(200), elapsedMs: 500, firstTokenMs: 100);

        Assert.Equal(500f, timings.TokensInSec, 0.01f);
        Assert.NotEqual(timings.Tokens, timings.TokensInSec);
    }

    /// <summary>
    /// The whole generation in one delta (window ≈ 0) must not divide by zero or go to infinity: the
    /// floor keeps it finite, and the floor is small enough to stay plausible.
    /// </summary>
    [Fact]
    public void InstantAnswer_StaysFinite()
    {
        var timings = new MessageTimings();

        MessageTimingsCalculator.Apply(timings, Capture(20), elapsedMs: 700, firstTokenMs: 700);

        Assert.True(float.IsFinite(timings.TokensInSec));
        Assert.True(timings.TokensInSec > 0);
        Assert.Equal(
            (float)(20 / MessageTimingsCalculator.MinimumGenerationWindowSeconds),
            timings.TokensInSec, 0.01f);
    }

    /// <summary>Nothing arrived yet: no tokens, no divide-by-zero, no negative span.</summary>
    [Fact]
    public void NoTokensYet_ReportsZeroSpeed()
    {
        var timings = new MessageTimings();

        MessageTimingsCalculator.Apply(timings, Capture(0), elapsedMs: 0, firstTokenMs: 0);

        Assert.Equal(0f, timings.TokensInSec);
        Assert.Equal(TimeSpan.Zero, timings.Total);
        Assert.Equal(TimeSpan.Zero, timings.FirstToken);
    }

    /// <summary>
    /// A clock that reports the first token AFTER the measured total (stale Stopwatch read, clock
    /// skew in a test seam) must not produce a negative window or a negative latency.
    /// </summary>
    [Fact]
    public void InconsistentClock_IsClamped()
    {
        var timings = new MessageTimings();

        MessageTimingsCalculator.Apply(timings, Capture(10), elapsedMs: 100, firstTokenMs: 5000);

        Assert.Equal(TimeSpan.FromMilliseconds(100), timings.FirstToken);
        Assert.Equal(TimeSpan.FromMilliseconds(100), timings.Total);
        Assert.True(timings.TokensInSec > 0);
    }

    [Fact]
    public void GenerationWindow_IsTheTimeAfterTheFirstToken()
    {
        Assert.Equal(4.0, MessageTimingsCalculator.GenerationWindowSeconds(5000, 1000), 0.001);
        Assert.Equal(
            MessageTimingsCalculator.MinimumGenerationWindowSeconds,
            MessageTimingsCalculator.GenerationWindowSeconds(700, 700), 0.001);
    }
}
