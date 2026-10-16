using System.Runtime.CompilerServices;

namespace UIBlazor.Services.AgentFramework;

/// <summary>
/// Hard-race helpers for streaming iteration: begins a MoveNext and, when the
/// cancellation token fires while the low-level HTTP read is stuck (WASM fetch and
/// the VS Code proxy cannot abort a streaming body once headers are received),
/// observes the cancellation immediately instead of waiting until the response ends.
/// Both AF gateways (the single-shot adapter and the tool-invocation loop) route their raw update
/// streams through <see cref="GuardAsync{T}"/>.
/// </summary>
internal static class AfCancelRace
{
    /// <summary>
    /// Shared wrapper over a raw gateway stream:
    /// - races each MoveNext against user cancellation so a stuck transport is abandoned
    ///   immediately instead of delaying cancellation until the response ends;
    /// - classifies transport errors and non-success HTTP into <see cref="LlmApiException"/>
    ///   (transient classification, same as the legacy hand-written client); the timeout
    ///   message is built lazily so the current HttpTimeoutSec shows up in the error;
    /// - disposes the enumerator; when the user already cancelled, the disposal is abandoned
    ///   fire-and-forget instead of freezing the unwind on dispose: an async iterator cannot
    ///   complete its DisposeAsync while an abandoned MoveNext is still unwinding, and a stuck
    ///   low-level read (WASM fetch / VS Code proxy cannot abort a streaming body once headers
    ///   are received) may take seconds — the whole cancel path must not wait for it.
    /// </summary>
    public static async IAsyncEnumerable<T> GuardAsync<T>(
        IAsyncEnumerable<T> updates,
        Func<string> timeoutMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        RaceGate? race = null)
    {
        var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        // The built-in race is a fallback for the common construction order. Callers that
        // also create linked CTSes on the same token should pass a pre-built gate from
        // BEFORE those CTSes were registered: token callbacks run in registration order,
        // and a linked CTS cancellation that unwinds an in-flight tool wait (~100ms)
        // lets the FICC MoveNext complete first, so the race loses and cancellation
        // only surfaces at the next consumer pull.
        using var gate = race ?? Build(cancellationToken);
        try
        {
            while (true)
            {
                // Checked BEFORE issuing MoveNext: the first synchronous segment of the next
                // model request (context serialization and HTTP prep inside the SDK) can block
                // the single WASM thread for many seconds, and a WhenAny race cannot preempt a
                // MoveNext whose execution never reaches its first await. Once the user has
                // cancelled, do not run that synchronous segment at all.
                cancellationToken.ThrowIfCancellationRequested();

                bool hasNext;
                try
                {
                    hasNext = await NextOrCancelledAsync(enumerator.MoveNextAsync(), gate.CancelledTask, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new LlmApiException(timeoutMessage());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new LlmApiException(ex.Message);
                }

                if (!hasNext)
                    yield break;

                yield return enumerator.Current; // outside try: no yield-in-catch restrictions
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // The user already cancelled: the in-flight MoveNext (if any) is abandoned and
                // the iterator cannot unwind until it finishes. Do not block the cancel path
                // on the disposal - abandon it in the background and swallow its exceptions.
                _ = AbandonAsync(enumerator);
            }
            else
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    // dispose raced the token - abandoned, nothing to surface
                }
            }
        }
    }

    /// <summary>
    /// Fire-and-forget disposal of an abandoned enumerator: every exception is swallowed
    /// (the stream is being torn down after cancellation, there is nothing to surface).
    /// </summary>
    private static async Task AbandonAsync(IAsyncDisposable enumerator)
    {
        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // abandoned - nothing to surface
        }
    }

    /// <summary>
    /// Creates the race source + registration to drive <see cref="NextOrCancelledAsync"/>.
    /// Must be disposed when the iteration ends.
    /// </summary>
    public static RaceGate Build(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetResult());
        return new RaceGate(tcs.Task, registration);
    }

    /// <summary>
    /// Awaits the next MoveNext result; throws OperationCanceledException(cancellationToken)
    /// when the token fires first. Fast path: a synchronously-completed MoveNext needs no
    /// allocation at all.
    /// </summary>
    public static async ValueTask<bool> NextOrCancelledAsync(
        ValueTask<bool> moveNext,
        Task cancelledTask,
        CancellationToken cancellationToken)
    {
        if (moveNext.IsCompletedSuccessfully)
        {
            return moveNext.Result;
        }

        var moveNextTask = moveNext.AsTask();
        var winner = await Task.WhenAny(moveNextTask, cancelledTask).ConfigureAwait(false);

        if (winner != moveNextTask && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await moveNextTask.ConfigureAwait(false);
    }

    public readonly struct RaceGate(Task cancelledTask, CancellationTokenRegistration registration) : IDisposable
    {
        public Task CancelledTask => cancelledTask;

        public void Dispose() => registration.Dispose();
    }
}
