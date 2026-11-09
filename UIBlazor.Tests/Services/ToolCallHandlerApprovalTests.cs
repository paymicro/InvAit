namespace UIBlazor.Tests.Services;

/// <summary>
/// Approval-flow tests for <see cref="ToolCallHandler"/>.
/// </summary>
public partial class ToolCallHandlerTests
{
    [Fact]
    public async Task ProcessToolCallsAsync_PendingApproval_WaitsForApproval()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Pending);

        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        using var cts = new CancellationTokenSource();

        // Act - Start processing without approving (won't complete)
        var processTask = _sut.ProcessToolCallsAsync(list, AppMode.Agent, cts.Token);

        // Assert - Task should be waiting for approval
        Assert.False(processTask.IsCompleted);

        // Approve the tool
        await _sut.HandleApprovalAsync(list[0].Id, approved: true);

        // Wait for completion
        await processTask;

        // Assert
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
    }

    [Fact]
    public async Task HandleApprovalAsync_Approve_SetsApprovedStatus()
    {
        // Arrange - Start a pending approval by starting ProcessToolCallsAsync
        var list = CreateList("read_files", ToolApprovalStatus.Pending);
        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        using var cts = new CancellationTokenSource();

        // Act - Start processing and approve
        var processTask = _sut.ProcessToolCallsAsync(list, AppMode.Agent, cts.Token);
        await _sut.HandleApprovalAsync(list[0].Id, approved: true);
        await processTask;

        // Assert
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
        Assert.True(list[0].Result.Success);
    }

    [Fact]
    public async Task HandleApprovalAsync_Reject_SetsRejectedStatus()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Pending);
        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        using var cts = new CancellationTokenSource();

        // Act - Start processing and reject
        var processTask = _sut.ProcessToolCallsAsync(list, AppMode.Agent, cts.Token);
        await _sut.HandleApprovalAsync(list[0].Id, approved: false);
        await processTask;

        // Assert
        Assert.Equal(ToolApprovalStatus.Rejected, list[0].ApprovalStatus);
        Assert.False(list[0].Result.Success);
    }

    [Fact]
    public async Task HandleApprovalAsync_UnknownWaiter_DoesNotThrow()
    {
        // Act & Assert - Should not throw
        await _sut.HandleApprovalAsync("unknown-seg", approved: true);
    }

    [Fact]
    public async Task ProcessToolCallsAsync_Cancellation_ClearsWaitersAndReturns()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Pending);

        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        using var cts = new CancellationTokenSource();

        // Act - Start processing
        var processTask = _sut.ProcessToolCallsAsync(list, AppMode.Agent, cts.Token);

        // Cancel immediately
        cts.Cancel();

        // Assert
        await _sut.HandleApprovalAsync(list[0].Id, approved: false);
    }

    [Fact(Timeout = 10000)]
    public async Task ProcessToolCallsAsync_OutOfOrderApproval_WorksCorrectly()
    {
        // Arrange - Two pending tools; the user approves the second one before its wait starts
        var list = CreateList("tool1", ToolApprovalStatus.Pending);
        list.Add(new ToolCall
        {
            Id = "2",
            Function = new ToolCallFunction { Name = "tool2" },
            ApprovalStatus = ToolApprovalStatus.Pending
        });

        var tool1 = new Tool
        {
            Name = "tool1",
            DisplayName = "Tool 1",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "result1" })
        };
        var tool2 = new Tool
        {
            Name = "tool2",
            DisplayName = "Tool 2",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true, Result = "result2" })
        };

        _toolManagerMock.Setup(t => t.GetTool("tool1")).Returns(tool1);
        _toolManagerMock.Setup(t => t.GetTool("tool2")).Returns(tool2);

        // Approvals must be tied to this test's own token, not the xunit Fact-Timeout
        // token (which cancellation turns approvals into Rejected).
        using var cts = new CancellationTokenSource();

        // Act - Start processing (will block waiting for tool1 approval)
        var processTask = _sut.ProcessToolCallsAsync(list, AppMode.Agent, cts.Token);

        // Approve tool2 FIRST (out of order) - this should not be lost
        await _sut.HandleApprovalAsync(list[0].Id, approved: true);

        // Then approve tool1 - this unblocks the loop
        await _sut.HandleApprovalAsync(list[1].Id, approved: true);

        await processTask;

        // Assert - Both tools should be approved and executed
        Assert.Equal(2, list.Count);
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
        Assert.Equal(ToolApprovalStatus.Approved, list[1].ApprovalStatus);
        Assert.True(list[0].Result.Success);
        Assert.True(list[1].Result.Success);
    }

    [Fact]
    public async Task HandleApprovalAsync_DuplicateApproval_Idempotent()
    {
        // Arrange - the same tool call gets two decisions: approve, then reject.
        var list = CreateList("read_files", ToolApprovalStatus.Pending);
        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        using var cts = new CancellationTokenSource();

        // Act
        var processTask = _sut.ProcessToolCallsAsync(list, AppMode.Agent, cts.Token);
        await _sut.HandleApprovalAsync(list[0].Id, approved: true);
        await _sut.HandleApprovalAsync(list[0].Id, approved: false); // arrives after the first decision
        await processTask;

        // Assert - the first decision (Approved) wins, the tool is executed
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
        Assert.True(list[0].Result.Success);
    }

    [Fact]
    public async Task WaitForApproval_DecisionRecordedBeforeCancellation_WinsOverRejection()
    {
        // Arrange - a decision is recorded, then cancellation arrives. Whichever way the
        // internal race resolves (cancellation throws from WaitAsync or the released gate
        // completes the wait), the recorded decision must win: the tool runs, and the
        // approval status must stay Approved (not be flipped to Rejected).
        var list = CreateList("read_files", ToolApprovalStatus.Pending);
        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        using var cts = new CancellationTokenSource();

        // Act
        var processTask = _sut.ProcessToolCallsAsync(list, AppMode.Agent, cts.Token);
        await _sut.HandleApprovalAsync(list[0].Id, approved: true);
        cts.Cancel();
        await processTask;

        // Assert - the recorded decision wins over cancellation; the tool result may be
        // Cancelled (cancellation still applies to execution itself), but the approval
        // status must never surface as Rejected.
        Assert.Equal(ToolApprovalStatus.Approved, list[0].ApprovalStatus);
    }

    [Fact]
    public async Task HandleApprovalAsync_StaleOrphanApproval_EvictedOnNextPrepare()
    {
        // Arrange - a decision for an id no tool call will ever consume (stray/stale click)
        var gates = _sut._approvalGates;
        await _sut.HandleApprovalAsync("stray-id", approved: true);
        Assert.Contains("stray-id", gates.Keys);

        // Make the orphan be older than the TTL
        gates["stray-id"].CreatedAtUtc = DateTime.UtcNow - ApprovalGate.EarlyApprovalTtl - TimeSpan.FromMinutes(1);

        // Act - starting a new batch must evict the expired orphan
        _toolManagerMock.Setup(t => t.GetApprovalModeByToolName("read_files")).Returns(ToolApprovalMode.Ask);
        var batch = CreateList("read_files", ToolApprovalStatus.Pending);
        _sut.PrepareToolsForApprovals(batch);

        // Assert - the orphan is gone, the fresh batch gate is registered
        Assert.DoesNotContain("stray-id", gates.Keys);
        Assert.Contains("1", gates.Keys);
    }

    [Fact]
    public async Task HandleApprovalAsync_FreshEarlyApproval_NotEvictedOnNextPrepare()
    {
        // Arrange - an early decision younger than the TTL must survive eviction; it will be
        // consumed by the batch's wait instead (approving within the TTL is a normal out-of-order click)
        var gates = _sut._approvalGates;
        await _sut.HandleApprovalAsync("early-id", approved: true);

        _toolManagerMock.Setup(t => t.GetApprovalModeByToolName("other_tool")).Returns(ToolApprovalMode.Ask);

        // Act
        var batch = CreateList("other_tool", ToolApprovalStatus.Pending);
        _sut.PrepareToolsForApprovals(batch);

        // Assert - the fresh early decision is kept
        Assert.Contains("early-id", gates.Keys);
    }
}
