using System.Diagnostics;

namespace UIBlazor.Utils;

/// <summary>
/// Унифицированный throttle-примитив: окно коалесцирования + гарантия trailing-доставки
/// финального состояния (последний Notify доезжает до action, пока примитив не
/// освобождён/сброшен). Заменяет собой бывшие EventThrottler (leading) и Debouncer
/// (trailing-only сэмплер на PeriodicTimer).
/// </summary>
/// <remarks>
/// Режимы:
/// <list type="bullet">
/// <item><see cref="Leading"/> = true: первый Notify после истёкшего окна исполняется
/// немедленно (событийный паттерн — UI-троттлинг стрима);</item>
/// <item><see cref="Leading"/> = false: Notify только планирует исполнение через окно
/// от первого триггера (debounce-паттерн — автосейв настроек).</item>
/// </list>
/// Thread-safe. Ошибки action глотаются (логируются в Console.Error).
/// <para>
/// Контракты, о которых легко забыть:
/// <list type="bullet">
/// <item><b>Ведущий (leading) fire с синхронным action блокирует Notify</b> на время
/// выполнения action — fire-and-forget гарантирован только для trailing-пути.</item>
/// <item>Асинхронный action МОЖЕТ исполняться параллельно сам с собой (trailing снимает
/// pending-флаг до вызова action). Потребитель, которому нужна сериализация, обеспечивает её сам.</item>
/// <item><see cref="Dispose"/> и <see cref="Reset"/> отбрасывают запланированный
/// trailing — финальное состояние в этом случае НЕ доставляется (это намеренно: оба
/// вызываются, когда результат уже не нужен).</item>
/// <item>Смена latency-профиля против старого Debouncer: окно считается от ПЕРВОГО
/// триггера (задержка ≤ интервала), а не от тика PeriodicTimer (там могло быть до 2× интервала).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class Throttle : IDisposable
{
    private readonly Action? _syncAction;
    private readonly Func<Task>? _asyncAction;
    private readonly int _intervalMs;
    private readonly bool _leading;
    private readonly object _lock = new();
    // Per-fire CTS: Reset отменяет ТОЛЬКО текущий запланированный trailing (не весь
    // throttle навсегда — общий CTS после первого Cancel убивал бы все последующие
    // trailing-исполнения, как это было бы с одиночным полем).
    private CancellationTokenSource? _pendingCts;

    private long _lastFireTicks;
    private bool _pending;
    private int _firingCount;
    private bool _disposed;

    public Throttle(Action action, int intervalMs = 100, bool leading = true)
    {
        if (intervalMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), intervalMs, "interval must be positive");
        _syncAction = action;
        _intervalMs = intervalMs;
        _leading = leading;
        // Window starts EXPIRED: the first Notify fires immediately in leading mode
        // (same contract as the old EventThrottler — Blazor does not throttle the initial render).
        _lastFireTicks = Stopwatch.GetTimestamp() - Stopwatch.Frequency * _intervalMs / 1000;
    }

    public Throttle(Func<Task> action, int intervalMs = 100, bool leading = true)
        : this(() => { }, intervalMs, leading)
    {
        _asyncAction = action;
    }

    /// <summary>Исполнять ли первый Notify немедленно (leading) или всегда через окно (trailing-only).</summary>
    public bool Leading => _leading;

    /// <summary>
    /// True, пока trailing-исполнение запланировано ИЛИ его action ещё выполняется
    /// (доступ для тестов и сигнал «финальное состояние ещё не доставлено»).
    /// Ведущий (leading) fire НЕ учитывается — только trailing-путь.
    /// </summary>
    public bool IsPending
    {
        // _firingCount покрывает окно выполнения action: сброс _pending ДО вызова action
        // (нужен для реентерантного Notify) иначе заставлял наблюдателей считать,
        // что финальное состояние уже доставлено, пока trailing ещё в полёте.
        get { lock (_lock) return _pending || _firingCount > 0; }
    }

    /// <summary>
    /// Сообщает об изменении. В leading-режиме исполняет action немедленно, если окно
    /// истекло; иначе коалесцирует в одно trailing-исполнение после окна.
    /// В trailing-only режиме всегда планирует исполнение через окно от первого триггера.
    /// </summary>
    public void Notify()
    {
        CancellationTokenSource? superseded = null;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (_leading)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(_lastFireTicks).TotalMilliseconds;
                if (elapsedMs >= _intervalMs)
                {
                    _lastFireTicks = Stopwatch.GetTimestamp();
                    // Немедленный fire ВЫТЕСНЯЕТ запланированный trailing: без отмены его
                    // CTS старый fire просыпался позже (ReferenceEquals ещё true — CTS не
                    // заменялся), и в одном окне получалось ДВА срабатывания.
                    if (_pending)
                    {
                        superseded = _pendingCts;
                        _pendingCts = null;
                        _pending = false;
                    }
                }
                else if (_pending)
                {
                    return; // trailing fire already scheduled
                }
                else
                {
                    _pending = true;
                    var cts = _pendingCts = new CancellationTokenSource();
                    _ = FireTrailingAsync(cts);
                    return;
                }
            }
            else
            {
                if (_pending)
                    return; // trailing fire already scheduled — window counts from the FIRST trigger
                _pending = true;
                var cts = _pendingCts = new CancellationTokenSource();
                _ = FireTrailingAsync(cts);
                return;
            }
        }

        // Outside the lock: cancel the superseded trailing, then fire immediately
        // (the callback may raise UI events that re-enter Notify).
        // Только Cancel — Dispose делает владелец (FireTrailingAsync).
        superseded?.Cancel();
        _ = InvokeActionAsync();
    }

    /// <summary>
    /// Исполняет action. Синхронный action выполняется на месте (до возврата Task),
    /// асинхронный — ожидается; сбой глотается (fire-and-forget контракт).
    /// </summary>
    private async Task InvokeActionAsync()
    {
        try
        {
            if (_asyncAction is not null)
                await _asyncAction();
            else
                _syncAction!();
        }
        catch (Exception ex)
        {
            LogFailure(ex);
        }
    }

    /// <summary>
    /// Ссылка на СВОЙ CTS передаётся в замыкание: во время выполнения action может прийти новый
    /// Notify и заменить _pendingCts на чужой — finally обязан чистить только собственный
    /// (dispose чужого без Cancel оставлял его Task.Delay живым, а _pending=false лгал IsPending).
    /// </summary>
    private async Task FireTrailingAsync(CancellationTokenSource ownCts)
    {
        // ВЛАДЕНИЕ CTS: единственный владелец — этот метод (он гарантированно доходит
        // либо до выхода, либо до catch/finally). Notify/Reset/Dispose делают только
        // Cancel(), НИКОГДА Dispose(): параллельный Dispose одного CTS из двух потоков —
        // UB по контракту BCL (двойное Timer.Close / затирание _registrations).
        try
        {
            try
            {
                await Task.Delay(_intervalMs, ownCts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // Reset/Dispose/supersede — trailing намеренно отброшен
            }

            lock (_lock)
            {
                if (_disposed || !ReferenceEquals(_pendingCts, ownCts))
                    return; // вытеснён другим fire — ownCts всё равно dispose'ится в finally
                // The window restarts here.
                _lastFireTicks = Stopwatch.GetTimestamp();
                // Clear the pending flag BEFORE the action: a re-entrant Notify DURING the
                // action must schedule a NEW trailing fire (the state changed after this
                // fire picked up its snapshot). With the flag still set, Notify returned
                // early and the change was lost (same "lost final state" class of bug).
                // _firingCount keeps IsPending == true while the action runs.
                _pending = false;
                _pendingCts = null;
                _firingCount++;
            }

            try
            {
                await InvokeActionAsync();
            }
            finally
            {
                lock (_lock)
                    _firingCount--;
            }
        }
        finally
        {
            ownCts.Dispose();
        }
    }

    private static void LogFailure(Exception ex)
        // ex.ToString() включает type+message+stack: без стека ошибку отложенного
        // StateHasChanged (Blazor-рендер) диагностировать почти невозможно.
        => _ = Console.Error.WriteLineAsync($"[Throttle] action failed: {ex}");

    /// <summary>
    /// Отменяет запланированный trailing-fire (без исполнения action).
    /// Вызывается потребителем, когда он уже отработал состояние сам (leading-путём),
    /// и redundant trailing-исполнение не нужно.
    /// </summary>
    public void Reset()
    {
        CancellationTokenSource? toCancel;
        lock (_lock)
        {
            if (_disposed || !_pending)
                return;
            _pending = false;
            toCancel = _pendingCts;
            _pendingCts = null;
        }
        // Только Cancel: Dispose делает владелец (FireTrailingAsync) — см. его комментарий.
        toCancel?.Cancel();
    }

    /// <summary>
    /// Освобождает ресурсы. Запланированный trailing-fire <b>отбрасывается</b>
    /// (финальное состояние НЕ доставляется — потребитель, которому это важно,
    /// должен выполнить финальный flush до Dispose).
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? toCancel;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            toCancel = _pendingCts;
            _pendingCts = null;
            _pending = false;
        }
        // Чтение и обнуление — под lock (раньше поле читалось вне lock: работало только
        // благодаря порядку записей, но было хрупко). Только Cancel — Dispose у владельца.
        toCancel?.Cancel();
    }
}
