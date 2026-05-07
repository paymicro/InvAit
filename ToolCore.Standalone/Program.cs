using System.Text.Json;

namespace ToolCore.Standalone;

class Program
{
    private static readonly ConsoleLogger _logger = new(verbose: true);
    private static readonly Lazy<McpProcessManager> _lazyManager = new(() => new McpProcessManager(_logger));
    private static McpProcessManager _manager => _lazyManager.Value;

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "mcp" => await HandleMcpCommandAsync([.. args.Skip(1)]),
                "exec" => await HandleExecCommandAsync([.. args.Skip(1)]),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => HandleUnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            _logger.Log($"Fatal error: {ex.Message}", "ERROR");
            return 1;
        }
    }

    private static int HandleUnknownCommand(string command)
    {
        _logger.Log($"Unknown command: {command}", "ERROR");
        PrintHelp();
        return 1;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            ToolHost - MCP Process Manager CLI

            Usage:
            ToolHost <command> [options]

            Commands:
            mcp start <serverId> --command <cmd> [--args "args"] [--cwd <dir>] [--env KEY=VALUE]
            Start an MCP server process
            Example: ToolHost mcp start filesystem --command npx --args "-y @modelcontextprotocol/server-filesystem ./test"

            mcp stop <serverId>
            Stop a running MCP server

            mcp list
            List all running MCP servers

            mcp stop-all
            Stop all running MCP servers

            mcp tools <serverId>
            List available tools from an MCP server (calls tools/list)

            mcp call <serverId> <method> [--params "{...}"] [--timeout <ms>]
            Call an MCP method
            Example: ToolHost mcp call filesystem tools/call --params "{\"name\":\"read_directory\",\"arguments\":{\"path\":\"."}}"

            mcp init <serverId>
            Initialize an MCP server (sends initialize + notifications/initialized)

            mcp send <serverId> <json>
            Send raw JSON-RPC message to server

            exec <command>
            Execute a shell command
            Example: ToolHost exec git status
            Example: ToolHost exec dotnet build -c Release

            help
            Show this help message

            Options:
            --verbose    Enable verbose logging
            --timeout    Set timeout in milliseconds (default: 10000 for MCP, 30000 for exec)
            """);
        return 0;
    }

    #region MCP Commands

    private static async Task<int> HandleMcpCommandAsync(string[] args)
    {
        if (args.Length == 0)
        {
            _logger.Log("MCP command requires subCommand", "ERROR");
            return 1;
        }

        var subCommand = args[0].ToLowerInvariant();

        return subCommand switch
        {
            "start" => await McpStartAsync([.. args.Skip(1)]),
            "stop" => await McpStopAsync([.. args.Skip(1)]),
            "list" => McpList(),
            "stop-all" => await McpStopAllAsync(),
            "tools" => await McpToolsAsync([.. args.Skip(1)]),
            "call" => await McpCallAsync([.. args.Skip(1)]),
            "init" => await McpInitAsync([.. args.Skip(1)]),
            "send" => await McpSendAsync([.. args.Skip(1)]),
            _ => HandleUnknownMcpCommand(subCommand)
        };
    }

    private static int HandleUnknownMcpCommand(string subCommand)
    {
        _logger.Log($"Unknown MCP subCommand: {subCommand}", "ERROR");
        return 1;
    }

    private static async Task<int> McpStartAsync(string[] args)
    {
        if (args.Length < 2)
        {
            _logger.Log("Usage: mcp start <serverId> --command <cmd> [--args \"\"args\"\"] [--cwd <dir>]", "ERROR");
            return 1;
        }

        var serverId = args[0];
        var command = GetOption(args, "--command");
        var cmdArgs = GetOption(args, "--args") ?? "";
        var cwd = GetOption(args, "--cwd") ?? Directory.GetCurrentDirectory();
        var env = ParseEnvOptions(args);

        if (string.IsNullOrEmpty(command))
        {
            _logger.Log("--command is required", "ERROR");
            return 1;
        }

        _logger.Log($"Starting MCP server '{serverId}': {command} {cmdArgs}");

        var result = await _manager.StartProcessAsync(serverId, command, cmdArgs, cwd, env);
        if (result.StartsWith("OK"))
        {
            _logger.Log(result);
            return 0;
        }
        else
        {
            _logger.Log(result, "ERROR");
            return 1;
        }
    }

    private static async Task<int> McpStopAsync(string[] args)
    {
        if (args.Length < 1)
        {
            _logger.Log("Usage: mcp stop <serverId>", "ERROR");
            return 1;
        }

        var serverId = args[0];
        _logger.Log($"Stopping MCP server '{serverId}'...");

        var result = await _manager.StopProcessAsync(serverId);
        if (result.StartsWith("OK"))
        {
            _logger.Log(result);
            return 0;
        }
        else
        {
            _logger.Log(result, "ERROR");
            return 1;
        }
    }

    private static int McpList()
    {
        var servers = _manager.GetRunningServers().ToList();
        if (servers.Count == 0)
        {
            Console.WriteLine("No running MCP servers.");
            return 0;
        }

        Console.WriteLine($"Running MCP servers ({servers.Count}):");
        foreach (var server in servers)
        {
            var status = _manager.IsProcessRunning(server) ? "running" : "stopped";
            Console.WriteLine($"  - {server} [{status}]");
        }
        return 0;
    }

    private static async Task<int> McpStopAllAsync()
    {
        var servers = _manager.GetRunningServers().ToList();
        if (servers.Count == 0)
        {
            Console.WriteLine("No running MCP servers.");
            return 0;
        }

        _logger.Log($"Stopping {servers.Count} MCP server(s)...");
        await _manager.StopAllProcessesAsync();
        _logger.Log("All servers stopped.");
        return 0;
    }

    private static async Task<int> McpToolsAsync(string[] args)
    {
        if (args.Length < 1)
        {
            _logger.Log("Usage: mcp tools <serverId>", "ERROR");
            return 1;
        }

        var serverId = args[0];
        var timeout = GetIntOption(args, "--timeout", 10000);

        if (!_manager.IsProcessRunning(serverId))
        {
            _logger.Log($"Server '{serverId}' is not running. Start it first with 'mcp start {serverId} ...'", "ERROR");
            return 1;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var request = new McpRequest
        {
            Id = requestId,
            Method = "tools/list",
            Params = new { }
        };

        _logger.Log($"Calling tools/list on '{serverId}'...");
        var response = await _manager.CallMethodAsync(serverId, requestId, JsonUtils.SerializeCompact(request), timeout);

        if (response.Success)
        {
            Console.WriteLine("\n=== Available Tools ===");
            Console.WriteLine(response.Payload);
            return 0;
        }
        else
        {
            _logger.Log($"Error: {response.Error}", "ERROR");
            return 1;
        }
    }

    private static async Task<int> McpCallAsync(string[] args)
    {
        if (args.Length < 2)
        {
            _logger.Log("Usage: mcp call <serverId> <method> [--params \"{...}\"] [--timeout <ms>]", "ERROR");
            return 1;
        }

        var serverId = args[0];
        var method = args[1];
        var paramsJson = GetOption(args, "--params") ?? "{}";
        var timeout = GetIntOption(args, "--timeout", 10000);

        if (!_manager.IsProcessRunning(serverId))
        {
            _logger.Log($"Server '{serverId}' is not running. Start it first with 'mcp start {serverId} ...'", "ERROR");
            return 1;
        }

        var requestId = Guid.NewGuid().ToString("N");
        object? parsedParams;

        try
        {
            parsedParams = JsonSerializer.Deserialize<object>(paramsJson);
        }
        catch
        {
            parsedParams = new { };
        }

        var request = new McpRequest
        {
            Id = requestId,
            Method = method,
            Params = parsedParams
        };

        _logger.Log($"Calling {method} on '{serverId}'...");
        var response = await _manager.CallMethodAsync(serverId, requestId, JsonUtils.SerializeCompact(request), timeout);

        if (response.Success)
        {
            Console.WriteLine("\n=== Response ===");
            Console.WriteLine(response.Payload);
            return 0;
        }
        else
        {
            _logger.Log($"Error: {response.Error}", "ERROR");
            return 1;
        }
    }

    private static async Task<int> McpInitAsync(string[] args)
    {
        if (args.Length < 1)
        {
            _logger.Log("Usage: mcp init <serverId>", "ERROR");
            return 1;
        }

        var serverId = args[0];
        var timeout = GetIntOption(args, "--timeout", 10000);

        if (!_manager.IsProcessRunning(serverId))
        {
            _logger.Log($"Server '{serverId}' is not running. Start it first with 'mcp start {serverId} ...'", "ERROR");
            return 1;
        }

        // Send initialize
        var requestId = Guid.NewGuid().ToString("N");
        var initRequest = new McpRequest
        {
            Id = requestId,
            Method = "initialize",
            Params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "ToolHost", version = "1.0" }
            }
        };

        _logger.Log($"Initializing '{serverId}'...");
        var response = await _manager.CallMethodAsync(serverId, requestId, JsonUtils.SerializeCompact(initRequest), timeout);

        if (!response.Success)
        {
            _logger.Log($"Initialize failed: {response.Error}", "ERROR");
            return 1;
        }

        _logger.Log("Initialize successful. Sending notifications/initialized...");

        // Send notifications/initialized
        var notification = new McpNotification
        {
            Method = "notifications/initialized",
            Params = new { }
        };

        await _manager.SendMessageAsync(serverId, JsonUtils.SerializeCompact(notification));
        _logger.Log("Server initialized and ready.");

        Console.WriteLine("\n=== Initialize Response ===");
        Console.WriteLine(response.Payload);

        return 0;
    }

    private static async Task<int> McpSendAsync(string[] args)
    {
        if (args.Length < 2)
        {
            _logger.Log("Usage: mcp send <serverId> <json>", "ERROR");
            return 1;
        }

        var serverId = args[0];
        var json = string.Join(" ", args.Skip(1));

        if (!_manager.IsProcessRunning(serverId))
        {
            _logger.Log($"Server '{serverId}' is not running.", "ERROR");
            return 1;
        }

        // Try to parse as MCP request to extract ID
        string? requestId = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(json);
            if (parsed.TryGetProperty("id", out var idElement))
            {
                requestId = idElement.GetString() ?? Guid.NewGuid().ToString("N");
            }
        }
        catch
        {
            requestId = Guid.NewGuid().ToString("N");
        }

        _logger.Log($"Sending raw message to '{serverId}'...");
        var result = await _manager.SendMessageAsync(serverId, json);

        if (result.StartsWith("OK"))
        {
            _logger.Log("Message sent.");
            // If it's a request (has ID), wait for response
            if (!string.IsNullOrEmpty(requestId))
            {
                _logger.Log("Waiting for response...");
                var timeout = GetIntOption(args, "--timeout", 10000);
                var response = await _manager.CallMethodAsync(serverId, requestId, "{}", timeout);
                Console.WriteLine("\n=== Response ===");
                Console.WriteLine(response.Success ? response.Payload : $"Error: {response.Error}");
            }
            return 0;
        }
        else
        {
            _logger.Log(result, "ERROR");
            return 1;
        }
    }

    #endregion

    #region Exec Commands

    private static async Task<int> HandleExecCommandAsync(string[] args)
    {
        if (args.Length == 0)
        {
            _logger.Log("Usage: exec <command>", "ERROR");
            return 1;
        }

        var command = string.Join(' ', args);

        _logger.Log($"Executing: {command}");

        var executor = new ProcessExecutor(_logger);
        var result = await executor.ExecuteBashAsync(command, Directory.GetCurrentDirectory(), 30000);

        Console.WriteLine("\n=== Output ===");
        Console.WriteLine(result.Output);

        if (!string.IsNullOrEmpty(result.Error))
        {
            Console.WriteLine("\n=== Error ===");
            Console.WriteLine(result.Error);
        }

        Console.WriteLine($"\nExit code: {result.ExitCode}");

        return result.Success ? 0 : 1;
    }

    #endregion

    #region Helpers

    private static string? GetOption(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index >= 0 && index + 1 < args.Length)
        {
            return args[index + 1];
        }
        return null;
    }

    private static int GetIntOption(string[] args, string option, int defaultValue)
    {
        var value = GetOption(args, option);
        if (int.TryParse(value, out var result))
        {
            return result;
        }
        return defaultValue;
    }

    private static Dictionary<string, string>? ParseEnvOptions(string[] args)
    {
        var envVars = args
            .Where(a => a.StartsWith("--env ") || a.Contains('='))
            .Select(a => a.Replace("--env ", ""))
            .Where(a => a.Contains('='))
            .Select(a => a.Split('=', 2))
            .ToDictionary(s => s[0], s => s.Length > 1 ? s[1] : "");

        return envVars.Count > 0 ? envVars : null;
    }

    #endregion
}
