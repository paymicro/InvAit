namespace UIBlazor.Utils;

public sealed class Debouncer : IDisposable
{
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private bool _pending;
    private readonly Func<Task> _action;

    public Debouncer(TimeSpan delay, Func<Task> action)
    {
        _timer = new PeriodicTimer(delay);
        _action = action;
        _ = RunLoopAsync();
    }

    public void Trigger() => _pending = true;

    private async Task RunLoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                if (_pending)
                {
                    _pending = false;
                    await _action();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer.Dispose();
    }
}
