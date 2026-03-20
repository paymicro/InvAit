namespace UIBlazor.Utils;

public class Throttler(int intervalMs) : IDisposable
{
    private DateTime _lastExecution = DateTime.MinValue;
    private bool _hasPendingWork;
    private bool _isWaiting;
    private CancellationTokenSource _cts = new();

    public bool ShouldRender(Action onTailUpdate)
    {
        var elapsed = (DateTime.Now - _lastExecution).TotalMilliseconds;

        if (elapsed >= intervalMs)
        {
            _lastExecution = DateTime.Now;
            _hasPendingWork = false;
            return true;
        }

        _hasPendingWork = true;

        if (!_isWaiting)
        {
            _isWaiting = true;
            var token = _cts.Token;

            _ = Task.Delay(intervalMs, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;

                _isWaiting = false;
                if (_hasPendingWork)
                {
                    _hasPendingWork = false;
                    onTailUpdate();
                }
            }, token);
        }

        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
