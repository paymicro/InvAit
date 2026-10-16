using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

// Radzen.Blazor (global usings in test infra) also declares ChatMessage.
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Idle-timeout planning in <see cref="AfCompletionsAdapter"/> (streaming): the timeout CTS
/// is re-armed after every delivered chunk, so the window covers the GAP between chunks —
/// not the whole body-read. The old code planned one full-window CTS over the entire
/// body-read: a long stream with steady chunks was cut mid-generation ("Request timed out").
/// </summary>
public class AfIdleTimeoutTests
{
    /// <summary>
    /// A chat client that yields chunks with a delay between them (and an optional delay
    /// before the first one). The delay respects the cancellation token — when the adapter's
    /// timeout CTS fires, the linked token cancels the delay and the OCE surfaces through
    /// AfCancelRace.GuardAsync as LlmApiException.
    /// </summary>
    private sealed class DelayedChatClient(TimeSpan initialDelay, TimeSpan gap, int chunks) : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (initialDelay > TimeSpan.Zero)
                await Task.Delay(initialDelay, cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < chunks; i++)
            {
                if (i > 0)
                    await Task.Delay(gap, cancellationToken).ConfigureAwait(false);

                yield return new ChatResponseUpdate(ChatRole.Assistant, $"chunk {i}");
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("streaming only");

        public object? GetService(Type serviceType, object? serviceKey) => null;
        public void Dispose() { }
    }

    private static AfCompletionsAdapter CreateAdapter(IChatClient fake, int timeoutSec)
        => new(
            () => new ConnectionProfile { Endpoint = "https://api.test", ApiKey = "key", Model = "test-model", Stream = true },
            new HttpClient(),
            () => timeoutSec,
            (_, _) => fake);

    private static async Task<List<ChatDelta>> Collect(IAsyncEnumerable<ChatDelta> stream)
    {
        var list = new List<ChatDelta>();
        await foreach (var delta in stream)
        {
            list.Add(delta);
        }
        return list;
    }

    [Fact(Timeout = 20000)]
    public async Task Streaming_StalledTransport_ThrowsLlmApiException()
    {
        // Arrange: the first chunk arrives, then the transport stalls for 3s with a 1s
        // idle window — the gap exceeds the window, the timeout must fire.
        var fake = new DelayedChatClient(TimeSpan.Zero, TimeSpan.FromSeconds(3), chunks: 3);
        var adapter = CreateAdapter(fake, timeoutSec: 1);
        var session = new ConversationSession { Id = "s" };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<LlmApiException>(() => Collect(adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [], session, new CompletionsResult(),
            TestContext.Current.CancellationToken)));

        Assert.Contains("timed out", exception.Message);
    }

    [Fact(Timeout = 20000)]
    public async Task Streaming_LongStreamWithSteadyChunks_NotCutByTimeout()
    {
        // Arrange: 4 chunks with 400ms gaps — the TOTAL body-read (~1.6s) exceeds the 1s
        // window, but every individual gap is inside it. The old full-window CTS cut this
        // stream mid-generation; the idle-timeout must deliver every chunk.
        var fake = new DelayedChatClient(TimeSpan.Zero, TimeSpan.FromMilliseconds(400), chunks: 4);
        var adapter = CreateAdapter(fake, timeoutSec: 1);
        var session = new ConversationSession { Id = "s" };

        // Act
        var deltas = await Collect(adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [], session, new CompletionsResult(),
            TestContext.Current.CancellationToken));

        // Assert: every chunk survived the (longer than the window) stream.
        Assert.Equal(4, deltas.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task Streaming_FirstByteDelayedBeyondWindow_ThrowsLlmApiException()
    {
        // Arrange: the connect + first byte phase stalls for 3s with a 1s window — the
        // initial CancelAfter covers the acquire phase.
        var fake = new DelayedChatClient(TimeSpan.FromSeconds(3), TimeSpan.Zero, chunks: 2);
        var adapter = CreateAdapter(fake, timeoutSec: 1);
        var session = new ConversationSession { Id = "s" };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<LlmApiException>(() => Collect(adapter.GetCompletionsAsync(
            [new WireMessage { Role = ChatMessageRole.User, Content = "hi" }], [], session, new CompletionsResult(),
            TestContext.Current.CancellationToken)));

        Assert.Contains("timed out", exception.Message);
    }
}
