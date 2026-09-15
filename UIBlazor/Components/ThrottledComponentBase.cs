using System.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace UIBlazor.Components;

/// <summary>
/// Base component class with render throttling to prevent excessive re-renders
/// when parameters change rapidly (e.g. streaming tool call arguments).
/// </summary>
public abstract class ThrottledComponentBase : ComponentBase, IDisposable
{
    private bool _disposed;
    private bool _firstRenderPending = true;
    private long _lastRenderTicks;
    private bool _forceTrailingRender;
    // internal for test access only
    protected internal CancellationTokenSource? PendingCts;

    /// <summary>
    /// Minimum interval between renders in milliseconds.
    /// Override in derived classes to customize.
    /// </summary>
    protected virtual int RenderIntervalMs => 500;

    /// <summary>
    /// Whether the component has pending changes that require a render.
    /// Override to add change detection (e.g. only render when data actually changed).
    /// </summary>
    protected virtual bool HasChanges() => true;

    /// <summary>
    /// Called after a render is allowed. Override to reset change-tracking state.
    /// </summary>
    protected virtual void OnRendered() { }

    protected override bool ShouldRender()
    {
        if (_disposed)
            return false;

        // Trailing render: it was scheduled when changes existed, so it must not
        // be vetoed by HasChanges(). Otherwise an identical re-render arriving
        // between scheduling and firing (e.g. the stream-completion cascade)
        // clears HasChanges and the last streamed content is never displayed.
        if (_forceTrailingRender)
        {
            _forceTrailingRender = false;
            _firstRenderPending = false;
            CancelPendingCts();
            _lastRenderTicks = Stopwatch.GetTimestamp();
            OnRendered();
            return true;
        }

        if (!HasChanges())
            return false;

        if (_firstRenderPending)
        {
            _lastRenderTicks = Stopwatch.GetTimestamp();
            _firstRenderPending = false;
            OnRendered();
            return true;
        }

        // Throttle: if rendered recently, defer
        var elapsedMs = Stopwatch.GetElapsedTime(_lastRenderTicks).TotalMilliseconds;
        if (elapsedMs < RenderIntervalMs)
        {
            PendingCts?.Cancel();
            PendingCts?.Dispose();
            PendingCts = new CancellationTokenSource();
            _ = DelayedStateHasChangedAsync(PendingCts.Token);
            return false;
        }

        CancelPendingCts();
        _lastRenderTicks = Stopwatch.GetTimestamp();
        OnRendered();
        return true;
    }

    private void CancelPendingCts()
    {
        PendingCts?.Cancel();
        PendingCts?.Dispose();
        PendingCts = null;
    }

    private async Task DelayedStateHasChangedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RenderIntervalMs, ct);
            _forceTrailingRender = true;
            StateHasChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // игнорим?
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (disposing)
            CancelPendingCts();
    }
}
