using System.Runtime.CompilerServices;

namespace UIBlazor.Tests.Services;

/// <summary>
/// ProcessStreamAsync tests for <seealso cref="ChatService"/>.
/// </summary>
public partial class ChatServiceTests
{
    [Fact]
    public async Task ProcessStreamAsync_WithReasoningOnly_UpdatesReasoningContent()
    {
        // Arrange
        var message = new VisualChatMessage();
        var deltas = CreateAsyncEnumerable(
            new ChatDelta { ReasoningContent = "Thinking step 1" },
            new ChatDelta { ReasoningContent = "Thinking step 2" }
        );

        // Act
        await CreateChatService().ProcessStreamAsync(message, deltas, null, null, null, new CompletionsResult(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(message.Content);
        Assert.Equal("Thinking step 1Thinking step 2", message.ReasoningContent);
    }

    [Fact]
    public async Task ProcessStreamAsync_WithContentOnly_UpdatesContent()
    {
        // Arrange
        var message = new VisualChatMessage();
        var deltas = CreateAsyncEnumerable(
            new ChatDelta { Content = "Hello" },
            new ChatDelta { Content = " World" }
        );

        // Act
        await CreateChatService().ProcessStreamAsync(message, deltas, null, null, null, new CompletionsResult(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Hello World", message.Content);
        Assert.Empty(message.ReasoningContent);
    }

    [Fact]
    public async Task ProcessStreamAsync_MixedContent_UpdatesBoth()
    {
        // Arrange
        var message = new VisualChatMessage();
        var deltas = CreateAsyncEnumerable(
            new ChatDelta { ReasoningContent = "Reasoning..." },
            new ChatDelta { Content = "Response" }
        );

        // Act
        await CreateChatService().ProcessStreamAsync(message, deltas, null, null, null, new CompletionsResult(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Response", message.Content);
        Assert.Equal("Reasoning...", message.ReasoningContent);
    }

    [Fact]
    public async Task ProcessStreamAsync_WithoutModelProvider_LeavesModelNull()
    {
        // Arrange
        var message = new VisualChatMessage();
        var deltas = CreateAsyncEnumerable(
            new ChatDelta { Content = "Hello" }
        );

        // Act
        await CreateChatService().ProcessStreamAsync(message, deltas, null, null, null, new CompletionsResult(), TestContext.Current.CancellationToken);

        // Assert - message.Model remains null when no provider is given
        Assert.Null(message.Model);
    }

    [Fact]
    public async Task ProcessStreamAsync_CallsOnContentUpdate_WithEachDelta()
    {
        // Arrange - onContentUpdate is called with individual deltas for incremental parsing
        // MessageParser.UpdateSegments handles accumulation internally via AppendToken
        var message = new VisualChatMessage();
        var capturedContents = new List<string>();
        var deltas = CreateAsyncEnumerable(
            new ChatDelta { Content = "Hello" },
            new ChatDelta { Content = " World" },
            new ChatDelta { Content = "!" }
        );

        // Act
        await CreateChatService().ProcessStreamAsync(message, deltas, capturedContents.Add, null, null, new CompletionsResult(), TestContext.Current.CancellationToken);

        // Assert - onContentUpdate receives individual deltas for incremental parsing
        Assert.Equal(3, capturedContents.Count);
        Assert.Equal("Hello", capturedContents[0]);
        Assert.Equal(" World", capturedContents[1]);
        Assert.Equal("!", capturedContents[2]);
        // The message has the correct final content
        Assert.Equal("Hello World!", message.Content);
    }

    [Fact]
    public async Task ProcessStreamAsync_TokensPerSecond_IsNotFlooredToOneSecond()
    {
        // Arrange - an instantly streamed answer: the only measured window is the generation itself.
        // A floor of one second made every sub-second answer report a number that was too LOW —
        // the divisor was "1 s", not the real elapsed time of the generation.
        var message = new VisualChatMessage();
        var capture = new CompletionsResult { CompletionTokens = 120 };
        var deltas = CreateAsyncEnumerable(
            new ChatDelta { Content = "answer" });

        // Act
        await CreateChatService().ProcessStreamAsync(
            message, deltas, null, null, null, capture, TestContext.Current.CancellationToken);

        // Assert - the reported speed is better than "all 120 tokens took a whole second".
        // (Equal would mean the 1 s floor was applied: 120 / 1 = 120 = Tokens.)
        Assert.True(message.Timings.TokensInSec > message.Timings.Tokens,
            $"TokensInSec {message.Timings.TokensInSec} must be measured, not floored to the token count " +
            $"(window {message.Timings.Total.TotalMilliseconds} ms, first token {message.Timings.FirstToken.TotalMilliseconds} ms)");
    }

    [Fact]
    public async Task ProcessStreamAsync_UpdatesTimingsInRealTime()
    {
        // Arrange - slow stream (delay before EVERY delta): the mid-stream snapshot
        // (taken at the first onStateChange, i.e. after the first delta is processed)
        // must already show ticking timings. The old test asserted >= 0 (always true) —
        // these asserts are falsifiable.
        var message = new VisualChatMessage();
        var deltas = SlowStream(
        [
            new ChatDelta { Content = "A" },
            new ChatDelta { Content = "BC" },
            new ChatDelta { Content = "DEF" }
        ]);

        double midStreamTotalMs = -1;
        double midStreamFirstTokenMs = -1;

        // Act - snapshot values at the first onStateChange (Timings object is mutated
        // in place, so the VALUES must be copied, not the reference)
        await CreateChatService().ProcessStreamAsync(
            message, deltas, null, null,
            () =>
            {
                if (midStreamTotalMs < 0 && message.Timings is { } t)
                {
                    midStreamTotalMs = t.Total.TotalMilliseconds;
                    midStreamFirstTokenMs = t.FirstToken.TotalMilliseconds;
                }
            },
            new CompletionsResult(), TestContext.Current.CancellationToken);

        // Assert - falsifiable: mid-stream (first delta) timings were already ticking
        Assert.True(midStreamTotalMs > 0,
            $"Timings.Total must be positive mid-stream, was {midStreamTotalMs}");
        Assert.True(midStreamFirstTokenMs > 0,
            $"Timings.FirstToken must be positive mid-stream, was {midStreamFirstTokenMs}");
        // Final timings are at least the mid-stream ones (monotonic clock)
        Assert.True(message.Timings.Total.TotalMilliseconds >= midStreamTotalMs,
            $"Final Total {message.Timings.Total.TotalMilliseconds} must be >= mid-stream {midStreamTotalMs}");
    }

    /// <summary>Stream with a 50ms delay before every delta — makes wall-clock timings measurable.</summary>
    private static async IAsyncEnumerable<ChatDelta> SlowStream(ChatDelta[] deltas)
    {
        foreach (var delta in deltas)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            yield return delta;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Reasoning token split: the badge (Timings.Tokens) shows the FULL generation
    //  (including reasoning) both during streaming and after completion — a lower
    //  final value would read as a bug; reasoning goes to Timings.ReasoningTokens.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessStreamAsync_WithReasoningTokens_BadgeShowsFullGeneration()
    {
        // Arrange - full completion is 100 tokens, of which 40 are reasoning
        var message = new VisualChatMessage();
        var capture = new CompletionsResult { CompletionTokens = 100, ReasoningTokens = 40 };
        var deltas = CreateAsyncEnumerable(new ChatDelta { Content = "Answer" });

        // Act
        await CreateChatService().ProcessStreamAsync(message, deltas, null, null, null, capture, TestContext.Current.CancellationToken);

        // Assert - finished badge keeps the full generation count, breakdown in ReasoningTokens
        Assert.Equal(100, message.Timings!.Tokens);
        Assert.Equal(40, message.Timings.ReasoningTokens);
        // generation speed is based on the full output, including reasoning
        Assert.True(message.Timings.TokensInSec > 0);
    }

    [Fact]
    public async Task ProcessStreamAsync_WhileStreaming_BadgeShowsFullGenerationIncludingReasoning()
    {
        // Arrange - during streaming there are no visible tokens yet while the model thinks;
        // a visible-only badge would look frozen at zero. Mid-stream snapshots must show
        // the full count; only the final (post-stream) value switches to visible tokens.
        var message = VisualChatMessage.CreateStreaming();
        var capture = new CompletionsResult { CompletionTokens = 100, ReasoningTokens = 40 };
        var midStreamSnapshots = new List<int>();
        var deltas = CreateAsyncEnumerable(
            new ChatDelta { ReasoningContent = "Thinking..." },
            new ChatDelta { Content = "Answer" });

        // Act
        await CreateChatService().ProcessStreamAsync(
            message, deltas,
            onContentUpdate: _ => { },
            onToolCallsUpdate: _ => { },
            onStateChange: () => midStreamSnapshots.Add(message.Timings!.Tokens),
            capture,
            TestContext.Current.CancellationToken);

        // Assert - the badge value is stable: full generation count mid-stream AND at the end
        Assert.NotEmpty(midStreamSnapshots);
        Assert.All(midStreamSnapshots, tokens => Assert.Equal(100, tokens));
        Assert.True(message.IsStreaming == false);
        Assert.Equal(100, message.Timings!.Tokens);
    }

    [Fact]
    public async Task ProcessStreamAsync_WithoutReasoning_BadgeShowsFullCompletion()
    {
        // Arrange - provider without details: ReasoningTokens stays 0
        var message = new VisualChatMessage();
        var capture = new CompletionsResult { CompletionTokens = 75 };
        var deltas = CreateAsyncEnumerable(new ChatDelta { Content = "Answer" });

        // Act
        await CreateChatService().ProcessStreamAsync(message, deltas, null, null, null, capture, TestContext.Current.CancellationToken);

        // Assert - behavior identical to pre-split versions
        Assert.Equal(75, message.Timings!.Tokens);
        Assert.Equal(0, message.Timings.ReasoningTokens);
    }

    /// <summary>
    /// A cancelled turn must still CLOSE its message. The old code returned from inside the
    /// delta loop, so the final <c>IsStreaming = false</c> never ran: the message stayed
    /// streaming forever - rendered as a frozen bubble AND filtered out of the wire history
    /// by the <c>IsStreaming</c> filter (the next turn silently lost it).
    /// </summary>
    [Fact]
    public async Task ProcessStreamAsync_CancelledMidStream_ClosesMessage()
    {
        // Arrange
        var message = VisualChatMessage.CreateStreaming();
        using var cts = new CancellationTokenSource();
        var deltas = CancelAfterFirstDelta(cts, TestContext.Current.CancellationToken);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateChatService().ProcessStreamAsync(
            message, deltas, null, null, null, new CompletionsResult(), cts.Token));

        // Assert
        Assert.Equal("A", message.Content);
        Assert.False(message.IsStreaming);
        Assert.NotNull(message.Timings);
    }

    /// <summary>Yields one delta, cancels the token, then observes the cancellation.</summary>
    private static async IAsyncEnumerable<ChatDelta> CancelAfterFirstDelta(
        CancellationTokenSource cts, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatDelta { Content = "A" };
        await cts.CancelAsync();
        await Task.Delay(Timeout.Infinite, ct);
    }
}
