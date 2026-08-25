using System.Diagnostics;
using McpHost;
using Shared.Contracts.McpHost;
using ToolCore.McpHost;

namespace ToolCore.Tests;

public class McpHostSupervisorIntegrationTests
{
    private static string GetHostDllPath()
        => TestAssetLocator.GetHostDllPath();

    private sealed class SilentLogger : ILogger
    {
        public void Log(string message, string level = "INFO")
        {
        }
    }

    private static McpHostSupervisorOptions CreateOptions(Action<McpHostSupervisorOptions>? configure = null)
    {
        var options = new McpHostSupervisorOptions
        {
            HostExecutablePath = GetHostDllPath(),
            SkipRuntimeCheck = true,
            StartTimeout = TimeSpan.FromSeconds(15),
        };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public async Task EnsureStarted_Ping_Succeeds()
    {
        var supervisor = new McpHostSupervisor(CreateOptions(), new SilentLogger());

        try
        {
            await supervisor.EnsureStartedAsync();
            Assert.True(supervisor.IsConnected);

            var ping = await supervisor.PingAsync();

            Assert.Equal(McpHostProtocol.Version, ping.Version);
            Assert.True(ping.Pid > 0);
        }
        finally
        {
            await supervisor.DisposeAsync();
            Assert.True(WaitForProcessExit(supervisor),
                "MCP host process should exit after supervisor disposal.");
        }
    }

    [Fact]
    public async Task Status_ReturnsSuccessWithUptime()
    {
        await using var supervisor = new McpHostSupervisor(CreateOptions(), new SilentLogger());
        await supervisor.EnsureStartedAsync();

        var response = await supervisor.SendRequestAsync(McpHostMethods.Status);

        Assert.True(response.Success, response.Error);
        Assert.True(response.Result.HasValue);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsError()
    {
        await using var supervisor = new McpHostSupervisor(CreateOptions(), new SilentLogger());
        await supervisor.EnsureStartedAsync();

        var response = await supervisor.SendRequestAsync("no_such_method");

        Assert.False(response.Success);
        Assert.Contains("no_such_method", response.Error);
    }

    [Fact]
    public async Task Shutdown_StopsHostProcess()
    {
        await using var supervisor = new McpHostSupervisor(CreateOptions(), new SilentLogger());
        await supervisor.EnsureStartedAsync();

        var response = await supervisor.SendRequestAsync(McpHostMethods.Shutdown);

        Assert.True(response.Success);
        Assert.True(WaitForCondition(() => !supervisor.IsConnected, TimeSpan.FromSeconds(5)),
            "Connection should drop after shutdown request.");
    }

    [Fact]
    public async Task Dispose_KillsHostProcess()
    {
        var supervisor = new McpHostSupervisor(CreateOptions(), new SilentLogger());
        await supervisor.EnsureStartedAsync();
        Assert.True(supervisor.IsConnected);

        await supervisor.DisposeAsync();

        Assert.True(WaitForProcessExit(supervisor), "MCP host process should exit after DisposeAsync.");
    }

    [Fact]
    public async Task ExternalKill_ThenEnsureStarted_RestartsNewProcess()
    {
        await using var supervisor = new McpHostSupervisor(CreateOptions(), new SilentLogger());
        await supervisor.EnsureStartedAsync();
        var firstPid = await GetHostPid(supervisor);

        KillHostProcess(supervisor);
        Assert.True(WaitForCondition(() => !supervisor.IsConnected, TimeSpan.FromSeconds(5)),
            "Contact should be lost after external kill.");

        await supervisor.EnsureStartedAsync();
        var secondPid = await GetHostPid(supervisor);

        Assert.NotEqual(firstPid, secondPid);
        Assert.True(supervisor.IsConnected);
    }

    [Fact]
    public async Task Watchdog_DetectsLostContact_AndAutoRestarts()
    {
        await using var supervisor = new McpHostSupervisor(CreateOptions(o =>
        {
            o.PingInterval = TimeSpan.FromMilliseconds(300);
            o.PingTimeout = TimeSpan.FromMilliseconds(500);
            o.PingFailureThreshold = 2;
            o.RestartBaseDelay = TimeSpan.FromMilliseconds(200);
        }), new SilentLogger());

        await supervisor.EnsureStartedAsync();
        Assert.True(supervisor.IsConnected);
        var firstPid = await GetHostPid(supervisor);

        KillHostProcess(supervisor);

        Assert.True(WaitForCondition(() => !supervisor.IsConnected, TimeSpan.FromSeconds(5)),
            "Watchdog should detect lost contact.");

        Assert.True(WaitForCondition(async () =>
        {
            try
            {
                if (!supervisor.IsConnected)
                    return false;
                var pid = (await supervisor.PingAsync()).Pid;
                return pid != firstPid;
            }
            catch
            {
                return false;
            }
        }, TimeSpan.FromSeconds(15)), "Supervisor should auto-restart the host with a fresh process.");
    }

    [Fact]
    public async Task SendRequest_PerCallTimeout_DoesNotBlockSubsequentRequests()
    {
        await using var supervisor = new McpHostSupervisor(CreateOptions(), new SilentLogger());
        await supervisor.EnsureStartedAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => supervisor.SendRequestAsync(
            McpHostMethods.CallTool,
            new McpCallToolParams
            {
                ServerId = "sleepy",
                Command = TestAssetLocator.GetAssetExePath("EchoMcpServer", "EchoMcpServer.exe"),
                ToolName = "sleep",
                Arguments = JsonSerializer.SerializeToElement(new { ms = 4000 }),
                TimeoutMs = 8000,
            },
            TimeSpan.FromMilliseconds(700)));

        var ping = await supervisor.PingAsync();
        Assert.True(ping.Pid > 0);
    }

    [Fact]
    public async Task EnsureStarted_MissingHostExecutable_ThrowsStartupExceptionWithPath()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "no-such-invgen-host-xyz", "missing-host.exe");
        var supervisor = new McpHostSupervisor(
            CreateOptions(o =>
            {
                o.HostExecutablePath = missingPath;
                o.SkipRuntimeCheck = true;
            }),
            new SilentLogger());

        try
        {
            var ex = await Assert.ThrowsAsync<McpHostStartupException>(() => supervisor.EnsureStartedAsync());
            Assert.Contains("missing-host.exe", ex.UserMessage);
        }
        finally
        {
            await supervisor.DisposeAsync();
        }
    }

    private static async Task<int> GetHostPid(McpHostSupervisor supervisor)
    {
        var ping = await supervisor.PingAsync();
        return ping.Pid;
    }

    private static void KillHostProcess(McpHostSupervisor supervisor)
    {
        var pid = GetHostPidSync(supervisor);
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }

    private static int GetHostPidSync(McpHostSupervisor supervisor)
        => GetHostPid(supervisor).GetAwaiter().GetResult();

    private static bool WaitForProcessExit(McpHostSupervisor supervisor)
    {
        return WaitForCondition(() => !IsHostProcessAlive(supervisor), TimeSpan.FromSeconds(10));
    }

    private static bool IsHostProcessAlive(McpHostSupervisor supervisor)
    {
        try
        {
            var pid = GetHostPidSync(supervisor);
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(100);
        }

        return condition();
    }

    private static bool WaitForCondition(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition().GetAwaiter().GetResult())
                return true;
            Thread.Sleep(150);
        }

        return condition().GetAwaiter().GetResult();
    }
}
