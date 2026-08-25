using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer().WithHttpTransport().WithTools<HttpEchoTools>();

var app = builder.Build();
app.MapMcp("/mcp");
app.Run();

[McpServerToolType]
public sealed class HttpEchoTools
{
    [McpServerTool(Name = "echo", Title = "Echo")]
    public static string Echo(string text) => $"http echo: {text}";

    [McpServerTool(Name = "get_env", Title = "Get env")]
    public static string GetEnv(string name) => Environment.GetEnvironmentVariable(name) ?? "<unset>";
}

namespace ToolCore.Tests.TestAssets
{
    public static class HttpEchoMarker
    {
    }
}
