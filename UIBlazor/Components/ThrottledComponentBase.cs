using Microsoft.AspNetCore.Components;

namespace UIBlazor.Components;

/// <summary>
/// Base component class with render throttling to prevent excessive re-renders
/// when parameters change rapidly (e.g. streaming tool call arguments).
/// </summary>
/// <remarks>
/// Leading-часть (разрешить рендер, если окно истекло) живёт в <see cref="ShouldRender"/>;
/// trailing-часть (отложенный StateHasChanged с гарантией доставки последнего состояния)
/// делегирована ядру <see cref="UIBlazor.Utils.Throttle"/> — тому же примитиву, что
/// используется для событий и автосейва. Один механизм trailing-гарантии вместо трёх.
/// </remarks>
public abstract class ThrottledComponentBase : ComponentBase, IDisposable
{
    private bool _disposed;
    private bool _firstRenderPending = true;
    private long _lastRenderTicks;
    private bool _forceTrailingRender;

    // Trailing-рендер через ядро Throttle: action = StateHasChanged + _forceTrailingRender.
    // Leading отключён — решение о leading-рендере принимает ShouldRender (нужен HasChanges).
    private readonly UIBlazor.Utils.Throttle _trailingRender;

    /// <summary>
    /// Minimum interval between renders in milliseconds.
    /// Override in derived classes to customize.
    /// </summary>
    /// <remarks>
    /// ВАЖНО: значение читается из конструктора базового класса (для trailing-ядра),
    /// поэтому override в наследнике ОБЯЗАН быть константой — не читать поля/свойства
    /// наследника (там ещё не инициализировано: получите 0 или NRE, throttle молча
    /// выключится, а ShouldRender будет читать живое значение — рассинхрон без ошибки).
    /// </remarks>
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

    /// <summary>
    /// True, пока trailing-рендер запланирован или его StateHasChanged ещё выполняется
    /// (тест-доступ; контракт ядра Throttle).
    /// </summary>
    protected internal bool IsTrailingPending => _trailingRender.IsPending;

    protected ThrottledComponentBase()
    {
        _trailingRender = new UIBlazor.Utils.Throttle(() =>
        {
            // Fire-and-forget может проснуться после Dispose (компонент ушёл со страницы,
            // сессия сменилась) — рендер disposed-компонента бросает из Blazor-ядра.
            if (_disposed)
                return;
            _forceTrailingRender = true;
            StateHasChanged();
        }, RenderIntervalMs, leading: false);
    }

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
            _lastRenderTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            OnRendered();
            return true;
        }

        if (!HasChanges())
            return false;

        if (_firstRenderPending)
        {
            _lastRenderTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _firstRenderPending = false;
            OnRendered();
            return true;
        }

        // Throttle: if rendered recently, defer to the trailing core
        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(_lastRenderTicks).TotalMilliseconds;
        if (elapsedMs < RenderIntervalMs)
        {
            _trailingRender.Notify();
            return false;
        }

        // Leading-рендер уже доставил состояние — redundant trailing не нужен
        // (тот же контракт, что у старого CancelPendingCts).
        _trailingRender.Reset();
        _lastRenderTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        OnRendered();
        return true;
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
            _trailingRender.Dispose();
    }
}
