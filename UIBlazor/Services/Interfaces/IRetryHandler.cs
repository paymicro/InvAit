namespace UIBlazor.Services.Interfaces;

public interface IRetryHandler
{
    /// <summary>
    /// Gets the delay in seconds for a retry attempt.
    /// </summary>
    int GetRetryDelay(int attempt);

    /// <summary>
    /// Waits for retry with countdown, respecting cancellation.
    /// </summary>
    Task WaitForRetryAsync(
        int delaySeconds,
        Action<int> onCountdownUpdate,
        CancellationToken cancellationToken);
}
