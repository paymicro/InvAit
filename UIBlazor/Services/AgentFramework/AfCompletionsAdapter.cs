using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Single-shot LLM gateway over Microsoft.Extensions.AI IChatClient (OpenAI SDK):
/// replaces the hand-written Chat Completions HTTP/SSE client from the legacy loop.
/// Per-call state (model/usage/tool_calls/finish_reason/error) is written exclusively
/// to resultCapture/targetSession - full isolation between main agent / sub-agent / compression.
/// No automatic tool execution: FunctionCallContent is accumulated outward and flows
/// into the existing approval path (ToolCallHandler).
/// Delta conversion is shared with the tool-loop path via <see cref="AfDeltaMapper"/>.
/// <para>
/// TIMEOUT OWNERSHIP. This adapter is the ONLY timeout owner of an LLM request. The
/// transport clients built by <see cref="AfChatClientFactory.Create"/> receive no timeout
/// (null), so a request is bounded exactly once, here.
/// </para>
/// <para>
/// That is a DELIBERATE behaviour change versus the retired <c>AfAgentLoop</c>, which
/// built the same client with a null timeout AND no CTS of its own: a main-agent turn
/// could wait forever and only a user cancel stopped it. Now the main agent shares the
/// sub-agent / compression semantics:
/// </para>
/// <list type="bullet">
/// <item>streaming: the window is an IDLE window, re-armed after every delivered chunk - it
/// covers connect + first byte and then the gap between chunks, never the whole body-read
/// (a long answer with steady chunks is not cut mid-generation);</item>
/// <item>non-streaming: the whole response IS one body-read, so the window is a total budget;</item>
/// <item><c>httpTimeoutSec &lt;= 0</c> disables the timeout entirely (same convention as
/// <c>TokensToCompress = 0</c>) - a zero-second CTS would otherwise fire before the request
/// is even sent and turn every call into a timeout error;</item>
/// <item>a burned window surfaces as <see cref="LlmApiException"/> (transient), so it spends
/// the retry budget instead of ending the run.</item>
/// </list>
/// </summary>
internal sealed class AfCompletionsAdapter(
    Func<ConnectionProfile> profile,
    HttpClient httpClient,
    Func<int> httpTimeoutSec,
    Func<ConnectionProfile, HttpClient, IChatClient>? chatClientFactory = null) : ICompletionsGateway
{
    // Default factory passes NO timeout to the transport clients (null): the adapter is
    // the single timeout owner. The old code planned the timeout twice (adapter CTS +
    // client CTS), and the client CTS covered the whole body-read, cutting long streams
    // mid-generation. The adapter's own CTS (idle-timeout for streaming) is enough.
    private readonly Func<ConnectionProfile, HttpClient, IChatClient> _chatClientFactory =
        chatClientFactory ?? ((p, h) => AfChatClientFactory.Create(p, h));

    public async IAsyncEnumerable<ChatDelta> GetCompletionsAsync(
        IReadOnlyList<WireMessage> messages,
        IEnumerable<Tool>? tools,
        ConversationSession targetSession,
        CompletionsResult resultCapture,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var currentProfile = profile();

        var chatMessages = AfMessageMapper.Map(messages);

        // Один проход: materialize + Count вместо Any() + Map() (двойная энумерация enumerable)
        IReadOnlyList<Tool>? toolList = tools as IReadOnlyList<Tool> ?? tools?.ToList();
        var chatTools = toolList is { Count: > 0 } ? AfToolMapper.Map(toolList) : null;

        // Single timeout owner: the adapter. The transport clients get NO timeout of
        // their own (null) — the old code planned the timeout TWICE (adapter CTS + client
        // CTS), and the 100s CTS covered the whole body-read, cutting long streams
        // mid-generation ("Request timed out").
        var timeoutSeconds = httpTimeoutSec();

        // <= 0 disables the timeout (same convention as TokensToCompress = 0): a CTS built
        // from zero would fire before the request leaves and turn every call into an error.
        using var timeoutCts = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using IChatClient client = _chatClientFactory(currentProfile, httpClient);

        var updates = await AcquireUpdatesAsync(
            client, currentProfile, chatMessages, chatTools, linkedCts, timeoutSeconds, cancellationToken).ConfigureAwait(false);

        if (currentProfile.Stream)
        {
            // Streaming: idle-timeout. The timer is re-armed before every MoveNext and
            // re-armed after every delivered chunk — a 100s window covers the connect +
            // first byte and then the gap between chunks, NOT the whole body-read. A
            // long stream with steady chunks never fires; a stalled transport does.
            var mapped = IdleTimeoutGuard(
                AfCancelRace.GuardAsync(
                    updates, () => $"Request timed out after {timeoutSeconds}s", cancellationToken),
                timeoutCts, timeoutSeconds);

            await foreach (var delta in AfDeltaMapper.PumpAsync(mapped, targetSession, resultCapture, cancellationToken))
            {
                yield return delta;
            }
        }
        else
        {
            // Non-streaming: the whole response is one request — the full-window CTS is
            // the correct scope; no re-arming needed.
            var mapped = AfCancelRace.GuardAsync(
                updates, () => $"Request timed out after {timeoutSeconds}s", cancellationToken);

            await foreach (var delta in AfDeltaMapper.PumpAsync(mapped, targetSession, resultCapture, cancellationToken))
            {
                yield return delta;
            }
        }
    }

    /// <summary>
    /// Non-streaming profiles get the whole response replayed as deltas (usage attached as
    /// a synthetic message). Streaming profiles return the raw IChatClient stream, already
    /// bound to the linked timeout token. Errors at this site are classified into
    /// <see cref="LlmApiException"/>; enumeration is guarded by AfCancelRace.GuardAsync.
    /// </summary>
    private static async Task<IAsyncEnumerable<ChatResponseUpdate>> AcquireUpdatesAsync(
        IChatClient client,
        ConnectionProfile profile,
        IReadOnlyList<ChatMessage> chatMessages,
        IReadOnlyList<AITool>? chatTools,
        CancellationTokenSource linkedCts,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var options = AfChatClientFactory.BuildChatOptions(profile, chatTools);

        try
        {
            if (!profile.Stream)
            {
                var response = await client.GetResponseAsync(chatMessages, options, linkedCts.Token).ConfigureAwait(false);
                if (response.Usage is { } usage)
                {
                    // UsageContent is consumed by the common mapper loop
                    response.Messages.Add(new ChatMessage(ChatRole.Assistant, [new UsageContent(usage)]));
                }

                return YieldComplete(response);
            }

            return client.GetStreamingResponseAsync(chatMessages, options, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpTimeoutSec - classified as transient, same as legacy
            throw new LlmApiException($"Request timed out after {timeoutSeconds}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not LlmApiException)
        {
            // Non-success HTTP and transport errors:
            // LlmApiException type preserves the transient classification of the previous hand-written client.
            // Inner preserves the original type/stack — avoids hiding the origin of bugs
            // (the message alone used to hide it).
            // An LlmApiException is EXCLUDED from this filter: re-wrapping it would build a new
            // instance with StatusCode = null, and IsRetryable reads exactly that status. A 401
            // from the transport would reach the caller as "status unknown" and be retried ten
            // times — the classification the transport already made must survive the wrapper.
            throw new LlmApiException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Re-arms the timeout CTS before every MoveNext and after every delivered update
    /// (idle-timeout): the window covers the gap between chunks, not the whole body-read.
    /// CancelAfter may be called repeatedly — each call restarts the timer.
    /// <para>
    /// <paramref name="timeoutSeconds"/> &lt;= 0 disables the timeout: the stream is passed
    /// through unguarded and only the caller's cancellation token can stop it.
    /// </para>
    /// </summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> IdleTimeoutGuard(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        CancellationTokenSource timeoutCts,
        int timeoutSeconds)
    {
        if (timeoutSeconds <= 0)
        {
            await foreach (var update in updates.ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        var window = TimeSpan.FromSeconds(timeoutSeconds);
        timeoutCts.CancelAfter(window);

        await foreach (var update in updates.ConfigureAwait(false))
        {
            timeoutCts.CancelAfter(window);
            yield return update;
        }
    }

    /// <summary>
    /// Non-streaming path: the whole response as a set of deltas.
    /// </summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> YieldComplete(ChatResponse response)
    {
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents)
            {
                ModelId = response.ModelId,
                FinishReason = response.FinishReason
            };
        }
    }
}
