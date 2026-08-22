using Microsoft.AspNetCore.Components;

namespace UIBlazor.Components;

/// <summary>
/// Base component class with render throttling to prevent excessive re-renders
/// when parameters change rapidly (e.g. streaming tool call arguments).
/// </summary>
public abstract class ThrottledComponentBase : ComponentBase, IDisposable
{
    private bool _shouldRender = true;
    private DateTime _lastRenderTime = DateTime.MinValue;
    private CancellationTokenSource? _pendingCts;

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
        if (!HasChanges())
            return false;

        if (_shouldRender)
        {
            _lastRenderTime = DateTime.Now;
            _shouldRender = false;
            OnRendered();
            return true;
        }

        // Throttle: if rendered recently, defer
        var elapsed = (DateTime.Now - _lastRenderTime).TotalMilliseconds;
        if (elapsed < RenderIntervalMs)
        {
            _pendingCts?.Cancel();
            _pendingCts = new CancellationTokenSource();
            _ = DelayedStateHasChangedAsync(_pendingCts.Token);
            return false;
        }

        _lastRenderTime = DateTime.Now;
        OnRendered();
        return true;
    }

    private async Task DelayedStateHasChangedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RenderIntervalMs, ct);
            _shouldRender = true;
            StateHasChanged();
        }
        catch (TaskCanceledException) { }
    }

    public void Dispose()
    {
        _pendingCts?.Cancel();
        _pendingCts?.Dispose();
    }
}
