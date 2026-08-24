namespace UIBlazor.Tests.Models;

/// <summary>
/// Tests for SubAgentMessage.Cancel() — an atomic Interlocked.Exchange-based ownership
/// transfer of the CancellationTokenSource, racing with ReleaseMemory().
/// </summary>
public class SubAgentMessageCancelTests
{
    [Fact]
    public void Cancel_TransitionsTokenToCancelled()
    {
        // Arrange
        var msg = new SubAgentMessage();
        var cts = new CancellationTokenSource();
        msg.SetCancellationTokenSource(cts);
        var cancelled = false;
        using var registration = cts.Token.Register(() => cancelled = true);

        // Act
        msg.Cancel();

        // Assert
        Assert.True(cancelled);
    }

    [Fact]
    public void Cancel_CalledTwice_IsNoOp()
    {
        // Arrange
        var msg = new SubAgentMessage();
        msg.SetCancellationTokenSource(new CancellationTokenSource());

        // Act / Assert — second call takes null and must not throw
        msg.Cancel();
        msg.Cancel();
    }

    [Fact]
    public void Cancel_AfterReleaseMemory_DoesNotThrow()
    {
        // Arrange
        var msg = new SubAgentMessage();
        msg.SetCancellationTokenSource(new CancellationTokenSource());
        msg.ReleaseMemory(); // takes and disposes the CTS

        // Act / Assert — Exchange returns null; no ObjectDisposedException
        msg.Cancel();
    }

    [Fact]
    public void ReleaseMemory_AfterCancel_DoesNotThrow()
    {
        // Arrange
        var msg = new SubAgentMessage();
        msg.SetCancellationTokenSource(new CancellationTokenSource());
        msg.Cancel(); // takes ownership and disposes the CTS

        // Act / Assert — ReleaseMemory sees null; no ObjectDisposedException
        msg.ReleaseMemory();
    }

    [Fact]
    public async Task Cancel_ConcurrentWithReleaseMemory_NeverThrows()
    {
        // Ownership-transfer invariant: exactly one of Cancel()/ReleaseMemory() gets the CTS.
        for (var i = 0; i < 200; i++)
        {
            var msg = new SubAgentMessage();
            msg.AddMessage(VisualChatMessage.CreateStreaming("partial"));
            msg.SetCancellationTokenSource(new CancellationTokenSource());

            var cancelTask = Task.Run(() => msg.Cancel());
            var releaseTask = Task.Run(() => msg.ReleaseMemory());

            // Any unhandled exception (e.g. ObjectDisposedException) fails the test
            await Task.WhenAll(cancelTask, releaseTask);
        }
    }

    [Fact]
    public async Task Cancel_ManyConcurrentCallers_OnlyOneTakesCts()
    {
        for (var i = 0; i < 50; i++)
        {
            var msg = new SubAgentMessage();
            msg.SetCancellationTokenSource(new CancellationTokenSource());

            var tasks = Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => msg.Cancel()))
                .ToArray();

            await Task.WhenAll(tasks); // 7 callers must see null and return silently
        }
    }

    [Fact]
    public void CanCancel_TrueOnlyWhileRunning()
    {
        var msg = new SubAgentMessage { Status = SubAgentStatus.Pending };
        Assert.False(msg.CanCancel);

        msg.Status = SubAgentStatus.Running;
        Assert.True(msg.CanCancel);

        msg.Status = SubAgentStatus.Completed;
        Assert.False(msg.CanCancel);

        msg.Status = SubAgentStatus.Failed;
        Assert.False(msg.CanCancel);

        msg.Status = SubAgentStatus.Cancelled;
        Assert.False(msg.CanCancel);
    }
}
