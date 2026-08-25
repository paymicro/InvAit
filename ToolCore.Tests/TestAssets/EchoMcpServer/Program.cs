using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ToolCore.Tests.TestAssets;

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "echo", Version = "1.0" },
    ToolCollection =
    [
        McpServerTool.Create((string text) => $"echo: {text}", new McpServerToolCreateOptions
        {
            Name = "echo",
            Description = "Echoes the given text back.",
        }),
        McpServerTool.Create((int a, int b) => a + b, new McpServerToolCreateOptions
        {
            Name = "add",
            Description = "Adds two numbers.",
        }),
        McpServerTool.Create((string name) =>
            Environment.GetEnvironmentVariable(name) ?? "<unset>", new McpServerToolCreateOptions
        {
            Name = "get_env",
            Description = "Returns an environment variable value or <unset>.",
        }),
        McpServerTool.Create((Func<string>)(() => throw new InvalidOperationException("boom")), new McpServerToolCreateOptions
        {
            Name = "fail",
            Description = "Always throws.",
        }),
        McpServerTool.Create((Func<string>)(() => string.Join("|", SpawnArgs.Values.Skip(1))), new McpServerToolCreateOptions
        {
            Name = "spawn_args",
            Description = "Returns this process's argv tail joined by '|'.",
        }),
        McpServerTool.Create((int ms) =>
        {
            Thread.Sleep(ms);
            return "slept";
        }, new McpServerToolCreateOptions
        {
            Name = "sleep",
            Description = "Sleeps for the given number of milliseconds.",
        }),
        McpServerTool.Create((Func<string>)(() => { Environment.Exit(7); return ""; }), new McpServerToolCreateOptions
        {
            Name = "exit",
            Description = "Kills the server process immediately.",
        }),
    ],
};

await using var server = McpServer.Create(new StdioServerTransport("echo"), options);
await server.RunAsync();

namespace ToolCore.Tests.TestAssets
{
    public static class EchoMarker
    {
    }

    public static class SpawnArgs
    {
        public static readonly string[] Values = Environment.GetCommandLineArgs();
    }
}
