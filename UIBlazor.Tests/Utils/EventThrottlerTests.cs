namespace UIBlazor.Tests.Utils;

/// <summary>
/// Tests for <see cref="EventThrottler"/> trailing-update delivery for high-frequency
/// event sources (e.g. sub-agent streaming deltas raising NotifyStateChanged per token).
/// </summary>
public class EventThrottlerTests
{
    [Fact]
    public void Notify_FirstCall_FiresImmediately()
    {
        // Arrange
        var count = 0;
        using var throttler = new EventThrottler(() => count++, intervalMs: 5000);

        // Act
        throttler.Notify();

        // Assert - first call is never throttled
        Assert.Equal(1, count);
        Assert.False(throttler.IsPending);
    }

    [Fact]
    public void Notify_BurstInsideWindow_CoalescesIntoTrailingFire()
    {
        // Arrange - simulates the token storm: N deltas inside the 100ms window
        var count = 0;
        using var throttler = new EventThrottler(() => count++, intervalMs: 100);

        // Act - first fires immediately, the rest coalesce
        throttler.Notify();
        for (var i = 0; i < 50; i++)
        {
            throttler.Notify();
        }

        // Assert - exactly one trailing fire scheduled, no immediate fires
        Assert.Equal(1, count);
        Assert.True(throttler.IsPending);

        // Trailing fire lands after the window
        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));
        Assert.Equal(2, count);
        Assert.False(throttler.IsPending);
    }

    [Fact]
    public void Notify_TokensOverTime_FiresAtMostOncePerWindow()
    {
        // Arrange - tokens arriving every 30ms over ~300ms: ~10 fires expected, not 10+
        var count = 0;
        using var throttler = new EventThrottler(() => count++, intervalMs: 100);

        // Act
        for (var i = 0; i < 10; i++)
        {
            throttler.Notify();
            Thread.Sleep(30);
        }

        // Settle the trailing fire
        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));

        // Assert - coalesced to a handful of fires instead of one per token
        // (10 tokens @ 30ms over a 100ms window ≈ 3-7 fires, far below 10)
        Assert.InRange(count, 2, 8);
    }

    [Fact]
    public void Notify_AfterWindowElapsed_FiresImmediatelyAgain()
    {
        // Arrange
        var count = 0;
        using var throttler = new EventThrottler(() => count++, intervalMs: 50);

        // Act - first fire, then wait past the window, then fire again
        throttler.Notify();
        Assert.Equal(1, count);
        Thread.Sleep(80);
        throttler.Notify();

        // Assert - second fire is immediate (window elapsed)
        Assert.Equal(2, count);
        Assert.False(throttler.IsPending);
    }

    [Fact]
    public void Notify_AfterDispose_NoFire()
    {
        // Arrange
        var count = 0;
        var throttler = new EventThrottler(() => count++, intervalMs: 50);
        throttler.Notify();
        Assert.Equal(1, count);

        // Act
        throttler.Dispose();
        throttler.Notify();

        // Assert - no immediate fire after dispose; trailing fire dropped
        Assert.Equal(1, count);
        Thread.Sleep(100);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Notify_ConcurrentBurst_ExactlyOneTrailingFire()
    {
        // Arrange - parallel streams (multi-branch fan-out) raising events concurrently
        var count = 0;
        using var throttler = new EventThrottler(() => Interlocked.Increment(ref count), intervalMs: 100);

        // Act
        throttler.Notify();
        Parallel.For(0, 100, _ => throttler.Notify());

        // Assert - one immediate + at most one trailing, no unbounded queue
        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));
        Assert.InRange(count, 2, 2);
    }
}

/// <summary>Test helpers for waiting out the trailing fire.</summary>
public static class EventThrottlerTestExtensions
{
    /// <summary>
    /// Waits until the trailing fire lands (IsPending == false) or the timeout expires.
    /// </summary>
    public static void WaitForTrailing(this EventThrottler throttler, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (throttler.IsPending && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }
}
