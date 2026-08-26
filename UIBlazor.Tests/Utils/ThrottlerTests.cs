namespace UIBlazor.Tests.Utils;

using UIBlazor.Utils;

/// <summary>
/// Tests for <see cref="Throttler"/> trailing-update delivery.
/// </summary>
public class ThrottlerTests
{
    [Fact]
    public async Task ShouldRender_LeadingCall_RendersImmediately()
    {
        using var throttler = new Throttler(60);
        var tailCalls = 0;

        Assert.True(throttler.ShouldRender(() => Interlocked.Increment(ref tailCalls)));
        await Task.Delay(120);

        Assert.Equal(0, Volatile.Read(ref tailCalls));
    }

    [Fact]
    public async Task ShouldRender_EveryVersion_IsEventuallyDisplayed()
    {
        // Simulates streaming: content versions arrive faster than the interval.
        // A version is DISPLAYED when ShouldRender returns true (leading render
        // shows current data synchronously) or when its tail callback fires.
        // After the stream goes quiet, the LAST version must be displayed.
        using var throttler = new Throttler(80);
        const int lastVersion = 12;
        var displayed = new bool[lastVersion + 1];
        var gate = new object();

        void MarkDisplayed(int version)
        {
            lock (gate)
            {
                displayed[version] = true;
            }
        }

        // Leading render — version 0
        if (throttler.ShouldRender(() => MarkDisplayed(0)))
        {
            MarkDisplayed(0);
        }

        // Burst: versions arriving every 25ms — some fall outside the window
        // and legitimately display immediately (leading edge)
        for (var i = 1; i <= lastVersion; i++)
        {
            await Task.Delay(25);
            var version = i;
            if (throttler.ShouldRender(() => MarkDisplayed(version)))
            {
                MarkDisplayed(version);
            }
        }

        // Quiet period: pending versions must be delivered by the tail
        await Task.Delay(300);

        lock (gate)
        {
            Assert.True(displayed[lastVersion],
                $"Final version {lastVersion} never displayed. " +
                $"Displayed: [{string.Join(", ", Enumerable.Range(0, displayed.Length).Where(v => displayed[v]))}]");
        }
    }
}
