namespace UIBlazor.Services;

/// <summary>
/// The ONE place that turns raw stream measurements into the timings a message badge shows.
/// <para>
/// It is a pure function on purpose. The numbers it produces are the user's only evidence of how
/// fast the model actually is, and they were previously computed inline inside
/// <c>ChatService.ProcessStreamAsync</c>, where no test could reach them without a <c>Stopwatch</c>
/// and a real stream — so the two defects that lived there (a doubled-up first-token wait and a
/// one-second floor) could not be pinned by a test at all.
/// </para>
/// <para>
/// The contract:
/// </para>
/// <list type="bullet">
/// <item><b>Total</b> — wall clock of the whole turn, including the wait for the first token.</item>
/// <item><b>FirstToken</b> — that wait, on its own. It is a latency, not a generation cost.</item>
/// <item><b>Tokens</b> — the FULL generation: answer <i>and</i> reasoning. The badge is a
/// generation counter; how much of it survives into the context is
/// <see cref="ConversationSession.TotalTokens"/>, which excludes reasoning.</item>
/// <item><b>TokensInSec</b> — <b>Tokens</b> over the <b>generation window</b>, i.e. from the first
/// emitted token to the last one. The latency to the first token is excluded from the divisor
/// because nothing is being generated while the model is thinking or the provider is queuing — and
/// <b>the same tokens that are counted must be the tokens that are timed</b>: the window starts at
/// the first token of ANY kind, so reasoning tokens are covered by the time they actually took.
/// A numerator that includes reasoning with a denominator measured over the answer alone (or the
/// other way round) produces a speed that belongs to no real process.</item>
/// </list>
/// <para>
/// The floor below exists only to keep the division finite when the whole generation arrives in one
/// delta. It must be small: a one-second floor silently under-reported every sub-second answer
/// (200 tokens in 0.4 s read "200 t/s" instead of "500 t/s"), and short answers are the common case.
/// </para>
/// </summary>
public static class MessageTimingsCalculator
{
    /// <summary>
    /// Shortest generation window the speed is divided by. Below this the measurements are noise
    /// (one delta carries the whole answer), but anything above it is measured as it happened.
    /// </summary>
    public const double MinimumGenerationWindowSeconds = 0.05;

    /// <summary>
    /// Stamps <paramref name="timings"/> from one stream measurement.
    /// </summary>
    /// <param name="timings">Target object, mutated in place (the same instance the view holds).</param>
    /// <param name="capture">State of the turn: full completion tokens and the reasoning share.</param>
    /// <param name="elapsedMs">Milliseconds since the request started.</param>
    /// <param name="firstTokenMs">
    /// Milliseconds until the FIRST token of any kind (reasoning counts: it is generated output).
    /// Zero means "no token arrived yet", which makes the whole elapsed time a latency.
    /// </param>
    public static void Apply(
        MessageTimings timings, CompletionsResult capture, double elapsedMs, double firstTokenMs)
    {
        var total = Math.Max(0, elapsedMs);
        var firstToken = Math.Clamp(firstTokenMs, 0, total);

        timings.Tokens = capture.CompletionTokens;
        timings.ReasoningTokens = capture.ReasoningTokens;
        timings.Total = TimeSpan.FromMilliseconds(total);
        timings.FirstToken = TimeSpan.FromMilliseconds(firstToken);
        timings.TokensInSec = (float)(capture.CompletionTokens / GenerationWindowSeconds(total, firstToken));
    }

    /// <summary>
    /// The window the generation itself took: everything after the first token. Public because the
    /// number is quoted in the docs and in test names ("the divisor") — and because a caller that
    /// ever needs to explain the speed should not re-derive it.
    /// </summary>
    public static double GenerationWindowSeconds(double elapsedMs, double firstTokenMs)
    {
        var windowSeconds = (Math.Max(0, elapsedMs) - Math.Clamp(firstTokenMs, 0, Math.Max(0, elapsedMs))) / 1000.0;
        return Math.Max(MinimumGenerationWindowSeconds, windowSeconds);
    }
}
