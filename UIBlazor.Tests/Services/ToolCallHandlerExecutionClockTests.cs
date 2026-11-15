namespace UIBlazor.Tests.Services;

/// <summary>
/// Execution-clock tests for <see cref="ToolCallHandler"/>: the start stamp and the measured
/// duration must cover the tool execution only — never the approval wait and never the wait
/// in the sequential batch queue.
/// </summary>
public partial class ToolCallHandlerTests
{
    [Fact]
    public async Task PendingApproval_ExecutionClockDoesNotStartBeforeApproval()
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

        // Act: start processing, keep the approval pending for a while
        var processTask = _sut.ProcessToolCallsAsync(list, AppMode.Agent, cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Assert: waiting for the user is not execution time
        Assert.Null(list[0].ExecutionStartedAtUtc);
        Assert.Null(list[0].ExecutionElapsedMs);
        Assert.False(list[0].IsExecuting);

        await _sut.HandleApprovalAsync(list[0].Id, approved: true);
        await processTask;

        // Assert: the clock covered the instant execution only, not the 300ms approval wait
        Assert.NotNull(list[0].ExecutionStartedAtUtc);
        Assert.NotNull(list[0].ExecutionElapsedMs);
        Assert.InRange(list[0].ExecutionElapsedMs!.Value, 0, 250);
        Assert.False(list[0].IsExecuting);
    }

    [Fact]
    public async Task QueuedCall_ExecutionClockStartsWithItsOwnExecution()
    {
        // Arrange: both calls are Approved up-front (Allow mode sets the whole batch),
        // but tools run sequentially — the second one must not show a running clock.
        var first = new ToolCall
        {
            Id = "1",
            ApprovalStatus = ToolApprovalStatus.Approved,
            Function = new ToolCallFunction { Name = "read_files", Arguments = """{"path":"1"}""" }
        };
        var second = new ToolCall
        {
            Id = "2",
            ApprovalStatus = ToolApprovalStatus.Approved,
            Function = new ToolCallFunction { Name = "read_files", Arguments = """{"path":"2"}""" }
        };
        var list = new List<ToolCall> { first, second };

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = async (args, _) =>
            {
                if (args?.Contains("\"1\"") == true)
                {
                    firstStarted.TrySetResult();
                    await release.Task;
                }

                return new VsToolResult { Success = true };
            }
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        // Act
        var processTask = _sut.ProcessToolCallsAsync(list, AppMode.Agent, TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert: the first call runs, the queued one has no clock at all
        Assert.True(first.IsExecuting);
        Assert.Null(second.ExecutionStartedAtUtc);
        Assert.Null(second.ExecutionElapsedMs);
        Assert.False(second.IsExecuting);

        release.TrySetResult();
        await processTask;

        // Assert: once its own execution is over, both carry a measured duration
        Assert.NotNull(first.ExecutionElapsedMs);
        Assert.NotNull(second.ExecutionElapsedMs);
        Assert.True(second.ExecutionElapsedMs >= 0);
    }

    [Fact]
    public async Task DeniedCall_NoExecutionClock()
    {
        // Arrange: rejected by the user — the tool body never runs
        var list = CreateList("read_files", ToolApprovalStatus.Rejected);
        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = (_, _) => Task.FromResult(new VsToolResult { Success = true })
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        // Act
        await _sut.ProcessToolCallsAsync(list, AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(list[0].Result!.Success);
        Assert.Null(list[0].ExecutionStartedAtUtc);
        Assert.Null(list[0].ExecutionElapsedMs);
    }

    [Fact]
    public async Task ToolNotAllowedInMode_NoExecutionClock()
    {
        // Arrange: the tool cannot execute in this mode, so there is nothing to measure
        var list = CreateList("write_file", ToolApprovalStatus.Approved);
        _toolManagerMock.Setup(t => t.GetTool("write_file")).Returns(new Tool
        {
            Name = "write_file",
            Category = ToolCategory.WriteFiles,
            Enabled = false,
            ExecuteAsync = null!,
            NativeTool = null!,
        });

        // Act
        await _sut.ProcessToolCallsAsync(list, AppMode.Chat, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(list[0].ExecutionStartedAtUtc);
        Assert.Null(list[0].ExecutionElapsedMs);
    }

    [Fact]
    public async Task ExecutedCall_MeasuresDurationAndKeepsStartStamp()
    {
        // Arrange
        var list = CreateList("read_files", ToolApprovalStatus.Approved);
        var tool = new Tool
        {
            Name = "read_files",
            DisplayName = "Read Files",
            Enabled = true,
            NativeTool = _nativeTool,
            ExecuteAsync = async (_, _) =>
            {
                await Task.Delay(110, TestContext.Current.CancellationToken);
                return new VsToolResult { Success = true };
            }
        };
        _toolManagerMock.Setup(t => t.GetTool("read_files")).Returns(tool);

        // Act
        await _sut.ProcessToolCallsAsync(list, AppMode.Agent, TestContext.Current.CancellationToken);

        // Assert: the measured interval covers the tool body, not a fixed overhead
        Assert.NotNull(list[0].ExecutionStartedAtUtc);
        Assert.NotNull(list[0].ExecutionElapsedMs);
        Assert.InRange(list[0].ExecutionElapsedMs!.Value, 100, 5_000);
    }
}
