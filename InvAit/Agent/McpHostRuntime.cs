using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using InvAit.Utils;
using ToolCore.McpHost;

namespace InvAit.Agent;

public static class McpHostRuntime
{
    private static readonly Lazy<McpHostSupervisor> _supervisor = new(CreateSupervisor);

    public static McpHostSupervisor Supervisor => _supervisor.Value;

    private static McpHostSupervisor CreateSupervisor()
    {
        var options = new McpHostSupervisorOptions
        {
            HostExecutablePath = ResolveHostPath(),
        };
        return new McpHostSupervisor(options, new VsLogger());
    }

    private static string ResolveHostPath()
    {
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(assemblyDir))
        {
            var candidate = Path.Combine(assemblyDir, "MCPHost", "InvAit.McpHost.exe");
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(assemblyDir, "InvAit.McpHost.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        var fallbackDir = Path.GetDirectoryName(typeof(McpHostRuntime).Assembly.Location) ?? ".";
        return Path.Combine(fallbackDir, "MCPHost", "InvAit.McpHost.exe");
    }

    public static async Task ShutdownAsync()
    {
        if (!_supervisor.IsValueCreated)
            return;

        try
        {
            await Supervisor.DisposeAsync();
        }
        catch (Exception ex)
        {
            await Logger.LogAsync($"MCP host shutdown error: {ex.Message}", "WARN");
        }
    }
}
