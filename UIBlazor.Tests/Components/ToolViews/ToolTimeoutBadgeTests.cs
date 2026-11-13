namespace UIBlazor.Tests.Components.ToolViews;

/// <summary>
/// Tests for <see cref="ToolTimeoutBadge"/>
/// </summary>
public class ToolTimeoutBadgeTests : BunitContext
{
    public ToolTimeoutBadgeTests()
    {
        // Tooltip inside the badge requires Radzen services (TooltipService)
        Services.AddRadzenComponents();
    }

    private static ToolCall CreateToolCall(ToolApprovalStatus? status = ToolApprovalStatus.Pending) => new()
    {
        Id = "call-1",
        ApprovalStatus = status,
        Function = new ToolCallFunction { Name = "bash", Arguments = "{}" }
    };

    [Fact]
    public void Render_WithPositiveTimeout_ShowsBadgeWithInitialTime()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Approved);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, 120_000));

        var badge = cut.Find(".tool-timeout-badge");
        Assert.NotNull(badge);
        Assert.Contains("2:00", cut.Markup);
        Assert.Contains("fa-hourglass", cut.Markup);
        Assert.Contains("fa-spin", cut.Markup);
    }

    [Fact]
    public void PendingCall_BadgeIsStatic_NoCountdown()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Pending);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, 120_000));

        // Static badge shows the FULL timeout; the clock has not started until approval
        Assert.Contains("2:00", cut.Markup);
        Assert.DoesNotContain("fa-spin", cut.Markup);

        // Wait past several tick intervals: the displayed time must not change
        Thread.Sleep(1200);
        cut.Render();
        Assert.Contains("2:00", cut.Markup);
    }

    [Fact]
    public void ApprovedCall_CountdownStarts()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Pending);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.Contains("2:00", cut.Markup);

        // Approval re-renders the parent block and re-passes the parameter: the countdown starts
        toolCall.ApprovalStatus = ToolApprovalStatus.Approved;
        cut.Render();
        Assert.Contains("fa-spin", cut.Markup);

        // After ~1.5s at least one second must tick away
        cut.WaitForAssertion(() =>
        {
            cut.Render();
            Assert.DoesNotContain("2:00", cut.Markup);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RejectedCall_BadgeHidden()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Pending);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.NotEmpty(cut.FindAll(".tool-timeout-badge"));

        toolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
        cut.Render();

        Assert.Empty(cut.FindAll(".tool-timeout-badge"));
    }

    [Fact]
    public void Render_WithZeroTimeout_DoesNotRender()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Approved);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, 0));

        Assert.Empty(cut.FindAll(".tool-timeout-badge"));
    }

    [Fact]
    public void Render_WithNegativeTimeout_DoesNotRender()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Approved);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, -1));

        Assert.Empty(cut.FindAll(".tool-timeout-badge"));
    }

    [Fact]
    public void ResultAppears_BadgeDisappears()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Approved);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, 120_000));

        Assert.NotEmpty(cut.FindAll(".tool-timeout-badge"));

        toolCall.Result = new ToolResult { Content = "done" };
        cut.Render();

        Assert.Empty(cut.FindAll(".tool-timeout-badge"));
    }

    [Fact]
    public void TimeoutExpires_ShowsZeroAndExpiredStyle()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Approved);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, 1));

        // After the first 500ms tick the remaining time reaches zero
        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find(".tool-timeout-expired");
            Assert.NotNull(badge);
            Assert.Contains("0:00", cut.Markup);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Expiry_WithResultPresent_BadgeDisappears()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Approved);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, 1));

        toolCall.Result = new ToolResult { Content = "done" };
        cut.Render();

        Assert.Empty(cut.FindAll(".tool-timeout-badge"));
        Assert.Empty(cut.FindAll(".tool-timeout-expired"));
    }

    [Fact]
    public void Expiry_WithoutResult_ShowsExpiredBadgeWithoutSpin()
    {
        var toolCall = CreateToolCall(ToolApprovalStatus.Approved);

        var cut = Render<ToolTimeoutBadge>(parameters => parameters
            .Add(p => p.ToolCall, toolCall)
            .Add(p => p.TimeoutMs, 1));

        cut.WaitForAssertion(() =>
        {
            var badge = cut.Find(".tool-timeout-expired");
            Assert.NotNull(badge);
            Assert.DoesNotContain("fa-spin", cut.Markup);
        }, TimeSpan.FromSeconds(5));
    }
}
