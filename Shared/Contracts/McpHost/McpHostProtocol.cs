using System.Text.Json;

namespace Shared.Contracts.McpHost;

/// <summary>
/// Wire protocol constants for the InvAit &lt;-&gt; McpHost IPC channel.
/// Framing: 4-byte little-endian payload length + UTF-8 JSON payload.
/// </summary>
public static class McpHostProtocol
{
    public const string Version = "1.0";

    /// <summary>Hard cap for a single frame payload (8 MB) to fail fast on desync.</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;
}

/// <summary>Request methods understood by McpHost.</summary>
public static class McpHostMethods
{
    public const string Ping = "ping";
    public const string Status = "status";
    public const string Shutdown = "shutdown";
    public const string ListTools = "list_tools";
    public const string CallTool = "call_tool";
    public const string StopServer = "stop_server";
    public const string StopAll = "stop_all";
}

public class McpHostRequest
{
    public long Id { get; set; }
    public string Method { get; set; } = string.Empty;
    public object? Params { get; set; }
}

public class McpHostResponse
{
    public long Id { get; set; }
    public bool Success { get; set; } = true;
    public JsonElement? Result { get; set; }
    public string? Error { get; set; }
}

public class McpHostPingResult
{
    public string Version { get; set; } = string.Empty;
    public int Pid { get; set; }
    public long UptimeMs { get; set; }
}

/// <summary>Launch info for an MCP stdio server; the host lazily starts/reuses it by ServerId.</summary>
public class McpServerLaunchInfo
{
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Executable or script command (e.g. "npx"). Resolved against PATH/PATHEXT by the host.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Argument array passed to the command verbatim (no re-parsing).</summary>
    public List<string>? Args { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>HTTP/SSE endpoint URL. When set, Url takes precedence over Command.</summary>
    public string? Url { get; set; }

    /// <summary>Extra HTTP headers for remote servers (e.g. Authorization).</summary>
    public Dictionary<string, string>? Headers { get; set; }

    public Dictionary<string, string>? Env { get; set; }
}

public class McpListToolsParams : McpServerLaunchInfo
{
}

public class McpCallToolParams : McpServerLaunchInfo
{
    public string ToolName { get; set; } = string.Empty;

    public JsonElement? Arguments { get; set; }

    public int TimeoutMs { get; set; } = 600_000;
}

public class McpStopServerParams
{
    public string ServerId { get; set; } = string.Empty;
}
