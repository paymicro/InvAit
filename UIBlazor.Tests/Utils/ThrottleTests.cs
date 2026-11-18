namespace UIBlazor.Tests.Utils;

/// <summary>
/// Tests for the unified <see cref="UIBlazor.Utils.Throttle"/> primitive:
/// leading mode (event throttling, ex-EventThrottler contract) and
/// trailing-only mode (debounce semantics, ex-Debouncer contract).
/// </summary>
public class ThrottleTests
{
    // ---------- Leading mode (ex-EventThrottler) ----------

    [Fact]
    public void Leading_FirstCall_FiresImmediately()
    {
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 5000);

        throttler.Notify();

        Assert.Equal(1, count);
        Assert.False(throttler.IsPending);
    }

    [Fact]
    public void Leading_BurstInsideWindow_CoalescesIntoTrailingFire()
    {
        // Simulates the token storm: N deltas inside the 100ms window
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 100);

        throttler.Notify();
        for (var i = 0; i < 50; i++)
        {
            throttler.Notify();
        }

        Assert.Equal(1, count);
        Assert.True(throttler.IsPending);

        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));
        Assert.Equal(2, count);
        Assert.False(throttler.IsPending);
    }

    [Fact]
    public void Leading_TokensOverTime_FiresAtMostOncePerWindow()
    {
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 100);

        for (var i = 0; i < 10; i++)
        {
            throttler.Notify();
            Thread.Sleep(30);
        }

        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));

        // Coalesced to a handful of fires instead of one per token
        Assert.InRange(count, 2, 8);
    }

    [Fact]
    public void Leading_AfterWindowElapsed_FiresImmediatelyAgain()
    {
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 50);

        throttler.Notify();
        Assert.Equal(1, count);
        Thread.Sleep(80);
        throttler.Notify();

        Assert.Equal(2, count);
        Assert.False(throttler.IsPending);
    }

    [Fact]
    public void Notify_AfterDispose_NoFire()
    {
        var count = 0;
        var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 50);
        throttler.Notify();
        Assert.Equal(1, count);

        throttler.Dispose();
        throttler.Notify();

        Assert.Equal(1, count);
        Thread.Sleep(100);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Leading_ConcurrentBurst_ExactlyOneTrailingFire()
    {
        // Parallel streams (multi-branch fan-out) raising events concurrently
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => Interlocked.Increment(ref count), intervalMs: 100);

        throttler.Notify();
        Parallel.For(0, 100, _ => throttler.Notify());

        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));
        Assert.Equal(2, count);
    }

    // ---------- Reentrancy / Reset / Dispose (review findings) ----------

    [Fact]
    public void ReentrantNotifyDuringTrailingAction_NextFireSurvives()
    {
        // Review finding: during FireAction a re-entrant Notify may schedule the NEXT
        // trailing fire — it must survive the finally-cleanup of the CURRENT fire
        // (the old code disposed the foreign CTS and reset _pending, losing the next fire).
        var count = 0;
        UIBlazor.Utils.Throttle? throttler = null;
        throttler = new UIBlazor.Utils.Throttle(() =>
        {
            count++;
            // Re-entrant Notify DURING the trailing action (like NotifyStateChanged
            // raising subscriber events that re-enter the source)
            throttler!.Notify();
        }, intervalMs: 60);

        throttler.Notify(); // immediate fire (count=1), re-entrant Notify schedules trailing A
        Assert.True(throttler.IsPending);

        // Wait for trailing A to FIRE (by count, not IsPending: the re-entrant Notify
        // inside A's action schedules B immediately, so IsPending never goes false here)
        ThrottleTestExtensions.WaitUntil(() => count == 2, TimeSpan.FromSeconds(2)); // A fired, B scheduled
        Assert.True(throttler.IsPending); // B must still be scheduled — not killed by A's cleanup

        ThrottleTestExtensions.WaitUntil(() => count == 3, TimeSpan.FromSeconds(2)); // B fired
        Assert.Equal(3, count);
        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));

        throttler.Dispose();
    }

    [Fact]
    public void Reset_CancelsScheduledTrailing_NoFire()
    {
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 200, leading: false);

        throttler.Notify();
        Assert.True(throttler.IsPending);

        throttler.Reset();
        Assert.False(throttler.IsPending);

        Thread.Sleep(400); // past the window — nothing must fire
        Assert.Equal(0, count);
    }

    [Fact]
    public void Reset_ThenNotifyAgain_SchedulesNewTrailing()
    {
        // Review finding: a single lifetime CTS would die after the first Reset —
        // subsequent trailing fires must still work (per-fire CTS).
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 60, leading: false);

        throttler.Notify();
        throttler.Reset();

        throttler.Notify(); // must schedule a NEW trailing fire
        Assert.True(throttler.IsPending);
        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));
        Assert.Equal(1, count);
    }

    [Fact]
    public void Dispose_DuringPendingTrailing_NoFire()
    {
        var count = 0;
        var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 200, leading: false);

        throttler.Notify();
        throttler.Dispose();

        Thread.Sleep(400);
        Assert.Equal(0, count);
    }

    [Fact]
    public void Leading_ImmediateFireSupercedesScheduledTrailing_NoDoubleFire()
    {
        // Second-pass review, real bug: an immediate leading fire did not cancel the
        // already-scheduled trailing fire. When the trailing woke up later, its CTS was
        // never replaced (ReferenceEquals still true) -> SECOND fire inside one window.
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 150);

        throttler.Notify(); // window starts expired -> immediate fire (1)
        Assert.Equal(1, count);

        throttler.Notify(); // inside window -> trailing scheduled
        Assert.True(throttler.IsPending);

        // Force the exact production condition of the bug: the window looks expired while a
        // trailing is still pending (a late trailing continuation under UI-thread pressure).
        // Deterministic vs. sleeping: sleeping past the window also lets the trailing fire.
        var lastFireTicks = typeof(UIBlazor.Utils.Throttle)
            .GetField("_lastFireTicks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        lastFireTicks.SetValue(throttler, System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency);

        throttler.Notify(); // immediate fire (2) — MUST supersede/cancel the pending trailing
        Assert.Equal(2, count);

        // The superseded trailing deadline is long past — it must NOT fire again
        Thread.Sleep(400);
        Assert.Equal(2, count);
        Assert.False(throttler.IsPending);
    }

    [Fact]
    public void IsPending_StaysTrueWhileActionRuns()
    {
        // Restored contract: IsPending means "final state not delivered yet", so it must
        // stay true WHILE the action executes (a false reset made settlement waits return
        // while the trailing call was still in flight).
        bool pendingDuringAction = false;
        UIBlazor.Utils.Throttle? throttler = null;
        throttler = new UIBlazor.Utils.Throttle(() =>
        {
            pendingDuringAction = throttler!.IsPending;
        }, intervalMs: 60, leading: false);

        throttler.Notify();
        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));

        Assert.True(pendingDuringAction);
        Assert.False(throttler.IsPending);

        throttler.Dispose();
    }

    [Fact]
    public async Task TrailingOnly_AsyncAction_IsPendingCoversAsyncCompletion()
    {
        // Async action: IsPending must not go false until the awaited action finished.
        var completed = false;
        using var throttler = new UIBlazor.Utils.Throttle(async () =>
        {
            await Task.Delay(80);
            completed = true;
        }, intervalMs: 50, leading: false);

        throttler.Notify();
        throttler.WaitForTrailing(TimeSpan.FromSeconds(3));

        Assert.True(completed); // WaitForTrailing waited for the async completion
        Assert.False(throttler.IsPending);
    }

    // ---------- Trailing-only mode (ex-Debouncer) ----------

    [Fact]
    public async Task TrailingOnly_FirstCall_DoesNotFireImmediately()
    {
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 5000, leading: false);

        throttler.Notify();

        Assert.Equal(0, count);
        Assert.True(throttler.IsPending);
    }

    [Fact]
    public async Task TrailingOnly_FiresOnceAfterWindow()
    {
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(() => count++, intervalMs: 100, leading: false);

        throttler.Notify();
        Thread.Sleep(50);
        throttler.Notify(); // must NOT extend the window (counts from the FIRST trigger)

        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));
        Assert.Equal(1, count);
        Assert.False(throttler.IsPending);
    }

    [Fact]
    public async Task TrailingOnly_AsyncAction_FiresAfterWindow()
    {
        var count = 0;
        using var throttler = new UIBlazor.Utils.Throttle(async () =>
        {
            await Task.Yield();
            count++;
        }, intervalMs: 80, leading: false);

        throttler.Notify();
        Assert.Equal(0, count);

        throttler.WaitForTrailing(TimeSpan.FromSeconds(2));
        Assert.Equal(1, count);
    }
}

/// <summary>Test helpers for waiting out the trailing fire.</summary>
public static class ThrottleTestExtensions
{
    /// <summary>
    /// Waits until the trailing fire lands (IsPending == false) or the timeout expires.
    /// </summary>
    public static void WaitForTrailing(this UIBlazor.Utils.Throttle throttler, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (throttler.IsPending && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }

    /// <summary>Polls an arbitrary condition until it holds or the timeout expires.</summary>
    public static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }
}
