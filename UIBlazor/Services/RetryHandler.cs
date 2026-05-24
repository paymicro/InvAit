namespace UIBlazor.Services;

public class RetryHandler : IRetryHandler
{
    public int GetRetryDelay(int attempt) => attempt switch
    {
        1 => 2,
        2 => 5,
        3 => 10,
        _ => 20
    };

    public async Task WaitForRetryAsync(
        int delaySeconds,
        Action<int> onCountdownUpdate,
        CancellationToken cancellationToken)
    {
        for (var i = delaySeconds; i > 0; i--)
        {
            onCountdownUpdate(i);
            await Task.Delay(1000, cancellationToken);
        }
        onCountdownUpdate(0);
    }
}
