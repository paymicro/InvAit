using System.Diagnostics;

namespace UIBlazor.Utils;

/// <summary>
/// Thread-safe trailing throttle for an event callback (the event-pattern sibling of
/// <see cref="ThrottledComponentBase"/>). The FIRST call fires immediately; calls arriving
/// inside the window are coalesced into one trailing fire after the window passes, so the
/// final state always reaches subscribers (trailing guarantee).
/// Used to throttle high-frequency sources (e.g. sub-agent streaming deltas raising
/// NotifyStateChanged on every token) instead of throttling each subscriber separately.
/// </summary>
public sealed class EventThrottler : IDisposable
{
    private readonly Action _action;
    private readonly int _intervalMs;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    private long _lastFireTicks;
    private bool _pending;
    private bool _disposed;

    public EventThrottler(Action action, int intervalMs = 100)
    {
        _action = action;
        _intervalMs = intervalMs;
        // Window starts EXPIRED: the first Notify fires immediately (same contract as
        // ThrottledComponentBase — Blazor does not throttle the initial render).
        _lastFireTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency * _intervalMs / 1000;
    }

    /// <summary>
    /// Reports an occurrence. Fires the action immediately when the window has elapsed,
    /// otherwise coalesces into a single trailing fire.
    /// </summary>
    public void Notify()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            var elapsedMs = Stopwatch.GetElapsedTime(_lastFireTicks).TotalMilliseconds;
            if (elapsedMs >= _intervalMs)
            {
                _lastFireTicks = Stopwatch.GetTimestamp();
                _pending = false;
            }
            else if (_pending)
            {
                return; // trailing fire already scheduled
            }
            else
            {
                _pending = true;
                _ = FireTrailingAsync(_cts.Token);
                return;
            }
        }

        // Fire outside the lock: the callback may raise UI events that re-enter Notify.
        _action();
    }

    /// <summary>True when a trailing fire is scheduled (test access).</summary>
    public bool IsPending
    {
        get { lock (_lock) return _pending; }
    }

    private async Task FireTrailingAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_intervalMs, ct).ConfigureAwait(false);

            lock (_lock)
            {
                if (_disposed)
                    return;
                _lastFireTicks = Stopwatch.GetTimestamp();
                _pending = false;
            }

            _action();
        }
        catch (OperationCanceledException)
        {
            // disposed while waiting — the trailing fire is intentionally dropped
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _cts.Cancel();
        _cts.Dispose();
    }
}
