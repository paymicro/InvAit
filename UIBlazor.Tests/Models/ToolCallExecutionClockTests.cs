namespace UIBlazor.Tests.Models;

/// <summary>
/// Execution clock of <see cref="ToolCall"/>: the single source of truth for the
/// <see cref="ToolTimeoutBadge"/> — it is stamped by the handler at the execution
/// boundary, never by the UI.
/// </summary>
public class ToolCallExecutionClockTests
{
    [Fact]
    public void NewCall_HasNoExecutionClock()
    {
        var call = new ToolCall();

        Assert.Null(call.ExecutionStartedAtUtc);
        Assert.Null(call.ExecutionElapsedMs);
        Assert.False(call.IsExecuting);
    }

    [Fact]
    public void MarkExecutionStarted_StampsStartAndMarksExecuting()
    {
        var call = new ToolCall();

        call.MarkExecutionStarted();

        Assert.NotNull(call.ExecutionStartedAtUtc);
        Assert.Null(call.ExecutionElapsedMs);
        Assert.True(call.IsExecuting);
    }

    [Fact]
    public void MarkExecutionStarted_IsIdempotent_KeepsFirstStamp()
    {
        var call = new ToolCall();

        call.MarkExecutionStarted();
        var first = call.ExecutionStartedAtUtc;

        Thread.Sleep(20);
        call.MarkExecutionStarted();

        Assert.Equal(first, call.ExecutionStartedAtUtc);
    }

    [Fact]
    public void MarkExecutionFinished_MeasuresFromStart()
    {
        var call = new ToolCall();
        call.ExecutionStartedAtUtc = DateTime.UtcNow.AddMilliseconds(-250);

        call.MarkExecutionFinished();

        Assert.NotNull(call.ExecutionElapsedMs);
        Assert.InRange(call.ExecutionElapsedMs!.Value, 250, 5_000);
        Assert.False(call.IsExecuting);
    }

    [Fact]
    public void MarkExecutionFinished_WithoutStart_KeepsClockEmpty()
    {
        // Denied / never-executed calls must not get a fabricated duration.
        var call = new ToolCall();

        call.MarkExecutionFinished();

        Assert.Null(call.ExecutionElapsedMs);
        Assert.False(call.IsExecuting);
    }

    [Fact]
    public void MarkExecutionFinished_IsIdempotent_KeepsFirstMeasurement()
    {
        var call = new ToolCall();
        call.MarkExecutionStarted();

        call.MarkExecutionFinished();
        var first = call.ExecutionElapsedMs;

        Thread.Sleep(20);
        call.MarkExecutionFinished();

        Assert.Equal(first, call.ExecutionElapsedMs);
    }

    [Fact]
    public void ExecutionElapsedMs_IsPersisted_StartStampIsNot()
    {
        var call = new ToolCall { ExecutionElapsedMs = 1_234 };
        call.MarkExecutionStarted();

        var json = JsonSerializer.Serialize(call);

        Assert.Contains("executionElapsedMs", json);

        var restored = JsonSerializer.Deserialize<ToolCall>(json)!;
        Assert.Equal(1_234, restored.ExecutionElapsedMs);
        // A restored call must not look like it is still running in this process.
        Assert.Null(restored.ExecutionStartedAtUtc);
        Assert.False(restored.IsExecuting);
    }
}
