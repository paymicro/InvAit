using System.Text.Json;
using Shared.Contracts.McpHost;
using ToolCore;
using ToolCore.McpHost;

namespace McpHost.Cli;

internal static class HostLocator
{
    public static string Resolve()
    {
        var configuration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "McpHost", "bin", configuration, "net10.0", "InvAit.McpHost.exe");
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException("InvAit.McpHost.exe not found. Build the McpHost project first.");
    }
}

internal sealed class ConsoleLogger : ILogger
{
    public void Log(string message, string level = "INFO") => Console.WriteLine($"[{level}] {message}");
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 2;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "ping" => await WithSupervisorAsync(async s => PrintObject(await s.PingAsync())),
                "status" => await WithSupervisorAsync(async s =>
                    PrintObject((await s.SendRequestAsync(McpHostMethods.Status)).Result)),
                "shutdown" => await WithSupervisorAsync(async s =>
                    PrintObject((await s.SendRequestAsync(McpHostMethods.Shutdown)).Result)),
                "list" => await RunListAsync(args),
                "call" => await RunCallAsync(args),
                _ => UnknownCommand(),
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(ex is McpHostStartupException ? ex.Message : ex.ToString());
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task<int> WithSupervisorAsync(Func<McpHostSupervisor, Task> action)
    {
        await using var supervisor = CreateSupervisor();
        await supervisor.EnsureStartedAsync();
        await action(supervisor);
        return 0;
    }

    private static async Task<int> RunListAsync(string[] args)
    {
        var launch = ParseLaunch(args);
        if (launch.ServerId == null)
            return Fail("list requires --server-id <name>");

        await using var supervisor = CreateSupervisor();
        await supervisor.EnsureStartedAsync();

        var response = await supervisor.SendRequestAsync(
            McpHostMethods.ListTools,
            new McpListToolsParams
            {
                ServerId = launch.ServerId,
                Command = launch.Command ?? string.Empty,
                Args = launch.Args,
                Url = launch.Url,
                Headers = launch.Headers,
                Env = launch.Env,
            },
            TimeSpan.FromSeconds(90));

        var toolsCount = PrintAndCountTools(response.Result, response.Error);
        Console.WriteLine($"tools: {toolsCount}");
        return response.Success && toolsCount > 0 ? 0 : 1;
    }

    private static async Task<int> RunCallAsync(string[] args)
    {
        string? serverId = null, toolName = null, argumentsJson = null;
        var timeoutMs = 600_000;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server-id" when HasValue(args, i): serverId = args[++i]; break;
                case "--tool" when HasValue(args, i): toolName = args[++i]; break;
                case "--args-json" when HasValue(args, i): argumentsJson = args[++i]; break;
                case "--timeout-ms" when HasValue(args, i): timeoutMs = int.Parse(args[++i]); break;
            }
        }

        if (serverId == null || toolName == null)
            return Fail("call requires --server-id <name> --tool <name> [--args-json '{...}']");

        await using var supervisor = CreateSupervisor();
        await supervisor.EnsureStartedAsync();

        var response = await supervisor.SendRequestAsync(
            McpHostMethods.CallTool,
            new McpCallToolParams
            {
                ServerId = serverId,
                ToolName = toolName,
                Arguments = argumentsJson == null ? null : JsonDocument.Parse(argumentsJson).RootElement.Clone(),
                TimeoutMs = timeoutMs,
            },
            TimeSpan.FromMilliseconds(timeoutMs) + TimeSpan.FromSeconds(30));

        PrintAndCountTools(response.Result, response.Error);
        return response.Success ? 0 : 1;
    }

    private static LaunchParameters ParseLaunch(string[] args)
    {
        string? serverId = null, url = null;
        Dictionary<string, string>? headers = null, env = null;
        List<string> positional = [];

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--":
                    break;
                case "--server-id" when HasValue(args, i): serverId = args[++i]; break;
                case "--url" when HasValue(args, i): url = args[++i]; break;
                case "--header" when i + 1 < args.Length:
                    (headers ??= []).AddPair(args[++i]);
                    break;
                case "--env" when i + 1 < args.Length:
                    (env ??= []).AddPair(args[++i]);
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        string? command = null;
        List<string>? list = null;

        if (positional.Count > 0)
        {
            command = positional[0];
            list = positional.Skip(1).ToList();
        }
        else if (url != null)
        {
            command = string.Empty;
        }

        return new LaunchParameters(serverId, command, list, url, headers, env);
    }

    private static void AddPair(this Dictionary<string, string> dictionary, string pair)
    {
        var separator = pair.IndexOf('=');
        if (separator <= 0)
            throw new ArgumentException($"Expected KEY=VALUE, got '{pair}'.");
        dictionary[pair[..separator]] = pair[(separator + 1)..];
    }

    private static bool HasValue(string[] args, int index) => index + 1 < args.Length;

    private static McpHostSupervisor CreateSupervisor() =>
        new(new McpHostSupervisorOptions { HostExecutablePath = HostLocator.Resolve() }, new ConsoleLogger());

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static void PrintObject(object? value)
        => Console.WriteLine(JsonSerializer.Serialize(value, Indented));

    private static int PrintAndCountTools(JsonElement? result, string? error)
    {
        if (!string.IsNullOrEmpty(error))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: " + error);
            Console.ResetColor();
            return 0;
        }

        if (result is not { } element)
        {
            Console.WriteLine("<empty result>");
            return 0;
        }

        Console.WriteLine(element.GetRawText());

        return element.ValueKind switch
        {
            JsonValueKind.Array => element.GetArrayLength(),
            JsonValueKind.Object when element.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array => tools.GetArrayLength(),
            _ => 0,
        };
    }

    private static int Fail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
        PrintHelp();
        return 2;
    }

    private static int UnknownCommand()
    {
        Console.WriteLine("Unknown command.");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        Usage:
          dotnet run --project McpHost.Cli -- ping | status | shutdown
          dotnet run --project McpHost.Cli -- list --server-id <name> [--env K=V] [--header K=V] (-- | --url <url>) [command args...]

          dotnet run --project McpHost.Cli -- call --server-id <name> --tool <name> [--args-json '{...}'] [--timeout-ms N]

        Examples:
          ... -- list --server-id st -- npx -y @modelcontextprotocol/server-sequential-thinking
          ... -- list --server-id gh --url https://api.githubcopilot.com/mcp/ --header Authorization=Bearer <PAT>
          ... -- call --server-id st --tool sequentialthinking --args-json "{\"thought\":\"hi\",\"thoughtNumber\":1,\"totalThoughts\":1,\"nextThoughtNeeded\":false}"
        """);
    }

    private sealed record LaunchParameters(
        string? ServerId,
        string? Command,
        List<string>? Args,
        string? Url,
        Dictionary<string, string>? Headers,
        Dictionary<string, string>? Env);
}
