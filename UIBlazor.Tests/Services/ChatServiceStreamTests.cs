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
    public async Task ProcessStreamAsync_UpdatesTimingsInRealTime()
    {
        // Arrange
        var message = new VisualChatMessage();
        var deltas = CreateAsyncEnumerable(
            new ChatDelta { Content = "A" },
            new ChatDelta { Content = "BC" },
            new ChatDelta { Content = "DEF" }
        );

        // Act
        await CreateChatService().ProcessStreamAsync(message, deltas, null, null, null, new CompletionsResult(), TestContext.Current.CancellationToken);

        // Assert - message.Timings is initialized and updated during streaming
        Assert.NotNull(message.Timings);
        Assert.True(message.Timings.TokensInSec >= 0);
        Assert.True(message.Timings.Total.TotalMilliseconds >= 0);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Reasoning token split: the badge (Timings.Tokens) must show only visible
    //  tokens (the context footprint), reasoning goes to Timings.ReasoningTokens.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessStreamAsync_WithReasoningTokens_SplitsVisibleAndReasoning()
    {
        // Arrange - full completion is 100 tokens, of which 40 are reasoning
        var message = new VisualChatMessage();
        var capture = new CompletionsResult { CompletionTokens = 100, ReasoningTokens = 40 };
        var deltas = CreateAsyncEnumerable(new ChatDelta { Content = "Answer" });

        // Act
        await CreateChatService().ProcessStreamAsync(message, deltas, null, null, null, capture, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(60, message.Timings!.Tokens);
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

        // Assert - every in-stream update includes reasoning; the final one does not
        Assert.NotEmpty(midStreamSnapshots);
        Assert.All(midStreamSnapshots, tokens => Assert.Equal(100, tokens));
        Assert.True(message.IsStreaming == false);
        Assert.Equal(60, message.Timings!.Tokens);
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
}
