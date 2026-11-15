namespace UIBlazor.Tests.Components.ToolViews;

/// <summary>
/// Tests for <see cref="ToolTimeoutBadge"/>.
/// <para>
/// The badge only represents the execution clock stamped on <see cref="ToolCall"/> by
/// <see cref="ToolCallHandler"/>: nothing is rendered while the call waits for approval,
/// the countdown is measured from <see cref="ToolCall.ExecutionStartedAtUtc"/> and the
/// measured <see cref="ToolCall.ExecutionElapsedMs"/> keeps the badge visible after the
/// result arrived.
/// </para>
/// </summary>
public class ToolTimeoutBadgeTests : BunitContext
{
    public ToolTimeoutBadgeTests()
    {
        // Tooltip inside the badge requires Radzen services (TooltipService)
        Services.AddRadzenComponents();
    }

    private static ToolCall CreateToolCall(ToolApprovalStatus? status = ToolApprovalStatus.Approved) => new()
    {
        Id = "call-1",
        ApprovalStatus = status,
        Function = new ToolCallFunction { Name = "bash", Arguments = "{}" }
    };

    /// <summary>Tool call whose execution started <paramref name="agoMs"/> ms ago.</summary>
    private static ToolCall CreateRunningCall(double agoMs, ToolApprovalStatus? status = ToolApprovalStatus.Approved)
    {
        var call = CreateToolCall(status);
        call.ExecutionStartedAtUtc = DateTime.UtcNow.AddMilliseconds(-agoMs);
        return call;
    }

    [Fact]
    public void PendingApproval_NoBadge()
    {
        // The approval clock is not the execution clock: nothing is shown while Pending.
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, CreateToolCall(ToolApprovalStatus.Pending))
            .Add(p => p.TimeoutMs, 120_000));

        Assert.Empty(cut.FindAll(".rz-badge"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void ApprovedButNotStarted_NoBadge()
    {
        // Regression: approval alone (e.g. Allow mode set the whole batch to Approved while
        // the call is still queued behind another tool) must NOT start the countdown.
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, CreateToolCall(ToolApprovalStatus.Approved))
            .Add(p => p.TimeoutMs, 120_000));

        Assert.Empty(cut.FindAll(".rz-badge"));
    }

    [Fact]
    public void RunningCall_ShowsRemainingFromStartStamp()
    {
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, CreateRunningCall(65_000))
            .Add(p => p.TimeoutMs, 120_000));

        var badge = cut.Find(".tool-timeout-badge");
        Assert.NotNull(badge);
        Assert.Contains("0:55", cut.Markup);
    }

    [Fact]
    public void ApprovalWait_IsNotCounted()
    {
        // Approved long ago, execution started 3s ago: the countdown shows 2:00 - 3s.
        var call = CreateToolCall(ToolApprovalStatus.Approved);
        call.ExecutionStartedAtUtc = DateTime.UtcNow.AddSeconds(-3);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.Contains("1:57", cut.Markup);
    }

    [Fact]
    public void FinishedCall_KeepsBadgeWithMeasuredElapsed()
    {
        var call = CreateToolCall();
        call.ExecutionStartedAtUtc = DateTime.UtcNow.AddSeconds(-5);
        call.ExecutionElapsedMs = 12_000;
        call.Result = new ToolResult { Content = "done" };

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.NotEmpty(cut.FindAll(".tool-timeout-elapsed"));
        Assert.Contains("0:12", cut.Markup);
        // Distinct icon so the finished state is not mistaken for a countdown
        Assert.Contains("fa-clock", cut.Markup);
        // The countdown classes must be gone
        Assert.Empty(cut.FindAll(".tool-timeout-badge"));
        Assert.Empty(cut.FindAll(".tool-timeout-expired"));
    }

    [Fact]
    public void FinishedInstantCall_BadgeHidden()
    {
        // Sub-second tools would render a useless "0:00" on every call.
        var call = CreateToolCall();
        call.ExecutionStartedAtUtc = DateTime.UtcNow;
        call.ExecutionElapsedMs = 400;
        call.Result = new ToolResult { Content = "done" };

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.Empty(cut.FindAll(".rz-badge"));
    }

    [Fact]
    public void RestoredSession_KeepsMeasuredElapsedWithoutStartStamp()
    {
        // e.g. a session restored from storage: the duration was persisted, the runtime
        // start stamp was not. The badge must still report the measured execution time
        // (that is exactly why ExecutionElapsedMs is serialized).
        var call = CreateToolCall();
        call.ExecutionElapsedMs = 5_000;
        call.Result = new ToolResult { Content = "done" };

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.NotEmpty(cut.FindAll(".tool-timeout-elapsed"));
        Assert.Contains("0:05", cut.Markup);
        // No start stamp ⇒ nothing is "running": no live countdown, no ticker.
        Assert.Empty(cut.FindAll(".tool-timeout-badge"));
        Assert.Empty(cut.FindAll(".tool-timeout-expired"));
    }

    [Fact]
    public void RejectedCall_NoBadge()
    {
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, CreateToolCall(ToolApprovalStatus.Rejected))
            .Add(p => p.TimeoutMs, 120_000));

        Assert.Empty(cut.FindAll(".rz-badge"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeout_RunningCall_HidesCountdown(int timeoutMs)
    {
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, CreateRunningCall(1_000))
            .Add(p => p.TimeoutMs, timeoutMs));

        Assert.Empty(cut.FindAll(".rz-badge"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeout_FinishedCall_StillShowsMeasuredElapsed(int timeoutMs)
    {
        // No configured limit only means there is nothing to count down from — the tool still
        // took a measurable time, and that is exactly what a finished badge reports.
        var call = CreateToolCall();
        call.ExecutionStartedAtUtc = DateTime.UtcNow.AddSeconds(-12);
        call.ExecutionElapsedMs = 12_000;
        call.Result = new ToolResult { Content = "done" };

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, timeoutMs));

        Assert.NotEmpty(cut.FindAll(".tool-timeout-elapsed"));
        Assert.Contains("0:12", cut.Markup);
    }

    [Fact]
    public void TimeoutExpires_ShowsZeroAndExpiredStyle()
    {
        // Started 50ms ago with a 1ms limit: the deadline is already gone.
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, CreateRunningCall(50))
            .Add(p => p.TimeoutMs, 1));

        Assert.NotEmpty(cut.FindAll(".tool-timeout-expired"));
        Assert.Contains("0:00", cut.Markup);
    }

    [Fact]
    public void ExpiredThenFinished_SwitchesToMeasuredElapsed()
    {
        var call = CreateRunningCall(50);
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, 1));

        Assert.NotEmpty(cut.FindAll(".tool-timeout-expired"));

        // The tool finished after the deadline: the badge must report the real duration.
        call.ExecutionElapsedMs = 5_000;
        call.Result = new ToolResult { Content = "done" };
        cut.Render();

        Assert.NotEmpty(cut.FindAll(".tool-timeout-elapsed"));
        Assert.Contains("0:05", cut.Markup);
        Assert.Empty(cut.FindAll(".tool-timeout-expired"));
    }

    [Fact]
    public async Task RunningCountdown_Ticks()
    {
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, CreateRunningCall(900))
            .Add(p => p.TimeoutMs, 120_000));

        // 120000 - 900 = 119.1s → still displayed as 2:00 (ceil)
        Assert.Contains("2:00", cut.Markup);

        await cut.WaitForAssertionAsync(() =>
        {
            cut.Render();
            Assert.DoesNotContain("2:00", cut.Markup);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FinishedTimerStops_NoFurtherRenders()
    {
        var call = CreateRunningCall(0);
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, 120_000));

        call.ExecutionElapsedMs = 3_000;
        call.Result = new ToolResult { Content = "done" };
        cut.Render();

        var rendersAfterFinish = cut.RenderCount;

        // Longer than two tick intervals: a live timer would re-render the component.
        Thread.Sleep(1_300);
        Assert.Equal(rendersAfterFinish, cut.RenderCount);
    }

    [Fact]
    public async Task StartStampAfterRender_MakesBadgeAppearWithoutParentRender()
    {
        // The handler stamps the clock in the service, not in the render cascade.
        var call = CreateToolCall();
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.Empty(cut.FindAll(".tool-timeout-badge"));

        call.MarkExecutionStarted();

        await cut.WaitForAssertionAsync(
            () => Assert.NotEmpty(cut.FindAll(".tool-timeout-badge")),
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FinishStampAfterRender_SwitchesToMeasuredElapsed()
    {
        var call = CreateToolCall();
        call.ExecutionStartedAtUtc = DateTime.UtcNow.AddSeconds(-3);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.Contains("1:57", cut.Markup);

        call.MarkExecutionFinished();

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.NotEmpty(cut.FindAll(".tool-timeout-elapsed"));
            Assert.Contains("0:03", cut.Markup);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Dispose_IsIdempotentAndStopsTicking()
    {
        var call = CreateRunningCall(0);
        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, call)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.NotNull(call.ExecutionStartedAtUtc);

        // A missing @implements IDisposable would make this a no-op and leak the timer.
        var disposable = Assert.IsAssignableFrom<IDisposable>(cut.Instance);
        disposable.Dispose();
        disposable.Dispose();

        var rendersAfterDispose = cut.RenderCount;
        Thread.Sleep(1_300);
        Assert.Equal(rendersAfterDispose, cut.RenderCount);
    }
}
