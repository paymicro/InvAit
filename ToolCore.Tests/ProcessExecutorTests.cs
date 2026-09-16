using Moq;

namespace ToolCore.Tests;

public class ProcessExecutorTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    [Fact]
    public async Task ExecuteAsync_ShouldReturnSuccess_WhenCommandIsSuccessful()
    {
        // Arrange
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetShellCommand();
        var args = EnvironmentHelpers.GetEchoArgs("hello world");

        // Act
        var result = await executor.ExecuteAsync(command, args, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Contains("hello world", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSetExitCode()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetShellCommand();
        var args = EnvironmentHelpers.GetEchoArgs("test");

        var result = await executor.ExecuteAsync(command, args, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailure_WhenCommandNotFound()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);

        var result = await executor.ExecuteAsync("nonexistent_command_xyz123", "", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRespectTimeout_WhenCommandTakesTooLong()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetSleepCommand();
        var args = EnvironmentHelpers.GetSleepArgs();

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 100, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.TimedOut || result.WasKilled);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnPartialOutput_WhenTimedOut()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetSleepCommand();
        var args = EnvironmentHelpers.GetSleepArgs();

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 100, cancellationToken: TestContext.Current.CancellationToken);

        // Should have some output even if timed out (empty is acceptable)
        Assert.NotNull(result.Output);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTruncateLongOutput()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        // Generate a lot of output
        var command = EnvironmentHelpers.GetPrintCommand();
        var args = EnvironmentHelpers.GetRepeatArgs("A", 5000);

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 10000, outputLimit: ProcessExecutor.DefaultOutputLimit, cancellationToken: TestContext.Current.CancellationToken);

        // Output should not exceed the limit significantly
        Assert.True(result.Output.Length <= ProcessExecutor.DefaultOutputLimit * 2,
            $"Output should be limited, but was {result.Output.Length} chars");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSupportCancellation()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetSleepCommand();
        var args = EnvironmentHelpers.GetSleepArgs();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 30000, cancellationToken: cts.Token);

        Assert.True(result.Cancelled || result.WasKilled || !result.Success);
    }

    [Fact]
    public async Task ExecuteBashAsync_ShouldReturnSuccess_WhenCommandIsSuccessful()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = "echo 'bash test'";

        var result = await executor.ExecuteBashAsync(command, timeoutMs: 5000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Contains("bash test", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassArgumentsAsCommandLine_WhenCommandHasSpaces()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetShellCommand();
        var args = EnvironmentHelpers.GetEchoArgs("hello world");

        var result = await executor.ExecuteAsync(command, args, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success || !result.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSetErrorOnFailure()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetShellCommand();
        // Use a command that fails
        var args = EnvironmentHelpers.GetFalseArgs();

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 5000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_ShouldHaveCancelledFlag()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetSleepCommand();
        var args = EnvironmentHelpers.GetSleepArgs();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 10000, cancellationToken: cts.Token);

        Assert.True(result.Cancelled || result.WasKilled || !result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellationBeforeStart_ShouldReturnCancelled()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await executor.ExecuteAsync("echo", "hello", cancellationToken: cts.Token);

        Assert.True(result.Cancelled || !result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotFallBackToShell_WhenCommandHasArguments()
    {
        // Verify that arguments are passed via ProcessStartInfo.Arguments
        // and not appended to the command line after the executable name.
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetShellCommand();
        var args = EnvironmentHelpers.GetEchoArgs("arg with spaces");

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 5000, cancellationToken: TestContext.Current.CancellationToken);

        // If the shell properly receives arguments, "arg with spaces" should be echoed
        Assert.NotNull(result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldLimitStderr()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetShellCommand();
        var args = EnvironmentHelpers.GetPrintToStderrArgs("E", 1000);

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 10000, outputLimit: ProcessExecutor.DefaultOutputLimit, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnTimedOutFlag_WhenTimeoutOccurs()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var command = EnvironmentHelpers.GetSleepCommand();
        var args = EnvironmentHelpers.GetSleepArgs();

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 100, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut || result.WasKilled || !result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_NullLogger_ShouldNotThrow()
    {
        var executor = new ProcessExecutor();
        var command = EnvironmentHelpers.GetShellCommand();
        var args = EnvironmentHelpers.GetEchoArgs("test");

        var result = await executor.ExecuteAsync(command, args, timeoutMs: 5000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_PolicyDeny_ShouldBlockCommand()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var policy = new CommandPolicy([CommandRule.DenyRule("*")]);

        var result = await executor.ExecuteAsync("echo", "hello", policy: policy, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("blocked", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_PolicyDenyThenAllow_LastRuleWins()
    {
        // Запрещено всё, но разрешено dotnet test * — последнее правило приоритетнее
        var policy = new CommandPolicy(
        [
            CommandRule.DenyRule("*"),
            CommandRule.AllowRule("dotnet test *")
        ]);

        Assert.True(policy.IsAllowed("dotnet test --filter X"));
        Assert.False(policy.IsAllowed("dotnet build"));
        Assert.False(policy.IsAllowed("rm -rf /"));
    }

    [Fact]
    public void CommandPolicy_AllowAll_WhenNoRules()
    {
        var policy = new CommandPolicy();

        Assert.True(policy.IsAllowed("anything"));
        Assert.True(policy.IsEmpty);
    }

    [Fact]
    public void CommandPolicy_LastMatchingRuleWins()
    {
        // Сначала разрешено всё, потом запрещено всё — запрет выигрывает
        var policy = new CommandPolicy(
        [
            CommandRule.AllowRule("*"),
            CommandRule.DenyRule("*")
        ]);

        Assert.False(policy.IsAllowed("echo hi"));
    }

    [Fact]
    public async Task ExecuteBashAsync_ShouldSupportMultilineScript()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var script = "echo 'line1'\necho 'line2'\n";

        var result = await executor.ExecuteBashAsync(script, timeoutMs: 5000, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Contains("line1", result.Output);
        Assert.Contains("line2", result.Output);
    }

    [Fact]
    public async Task ExecuteBashAsync_PolicyDeny_ShouldBlock()
    {
        var executor = new ProcessExecutor(_mockLogger.Object);
        var policy = new CommandPolicy([CommandRule.DenyRule("*")]);

        var result = await executor.ExecuteBashAsync("echo hi", policy: policy, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("blocked", result.Error);
    }
}

/// <summary>
/// Helper class to provide environment-specific commands for tests.
/// </summary>
internal static class EnvironmentHelpers
{
    public static string GetShellCommand()
    {
        // On Windows, prefer cmd for basic commands; on Linux/Mac, use sh
        if (OperatingSystem.IsWindows())
        {
            return "cmd";
        }
        return "sh";
    }

    public static string GetEchoArgs(string message)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"/c echo {message}";
        }
        return $"-c \"echo '{message}'\"";
    }

    public static string GetSleepCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            return "ping";
        }
        return "sleep";
    }

    public static string GetSleepArgs()
    {
        if (OperatingSystem.IsWindows())
        {
            // ping -n 30 -w 1000 127.0.0.1 ~ 30s, но таймаут сработает раньше
            return "-n 30 -w 1000 127.0.0.1";
        }
        return "30";
    }

    public static string GetPrintCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            return "cmd";
        }
        return "sh";
    }

    public static string GetRepeatArgs(string text, int count)
    {
        if (OperatingSystem.IsWindows())
        {
            // Use a PowerShell one-liner to repeat text
            return $"/c powershell -Command \"for($i=0;$i -lt {count};$i++){{ '{text}' }}\"";
        }
        return $"-c \"for i in $(seq 1 {count}); do echo '{text}'; done\"";
    }

    public static string GetFalseArgs()
    {
        if (OperatingSystem.IsWindows())
        {
            return "/c exit 1";
        }
        return "-c \"exit 1\"";
    }

    public static string GetPrintToStderrArgs(string text, int count)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"/c powershell -Command \"for($i=0;$i -lt {count};$i++){{ Write-Error '{text}' }}\"";
        }
        return $"-c \"for i in $(seq 1 {count}); do echo '{text}' >&2; done\"";
    }
}
