namespace ToolCore.McpHost;

public sealed class McpHostSupervisorOptions
{
    public string HostExecutablePath { get; set; } = string.Empty;

    public string? PipeName { get; set; }

    public bool SkipRuntimeCheck { get; set; }

    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int PingFailureThreshold { get; set; } = 3;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RestartBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan RestartMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Mirror of the host-side --idle-exit-ms safety belt.</summary>
    public int HostIdleExitMs { get; set; } = 60_000;

    public static McpHostSupervisorOptions CreateDefault(string hostPath) => new() { HostExecutablePath = hostPath };
}
