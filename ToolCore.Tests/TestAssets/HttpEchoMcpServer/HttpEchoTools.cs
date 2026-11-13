using ModelContextProtocol.Server;

namespace App;

[McpServerToolType]
public sealed class HttpEchoTools
{
    [McpServerTool(Name = "echo", Title = "Echo")]
    public static string Echo(string text) => $"http echo: {text}";

    [McpServerTool(Name = "get_env", Title = "Get env")]
    public static string GetEnv(string name) => Environment.GetEnvironmentVariable(name) ?? "<unset>";
}
