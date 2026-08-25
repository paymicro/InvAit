using McpHost;

var pipeName = args.Length > 1 && args[0] == "--pipe-name" ? args[1] : null;
var idleExitMs = 30_000;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--idle-exit-ms" && int.TryParse(args[i + 1], out var parsed))
        idleExitMs = Math.Max(1000, parsed);
}

if (string.IsNullOrWhiteSpace(pipeName))
{
    Console.Error.WriteLine("[mcphost] --pipe-name is required.");
    return 2;
}

Console.WriteLine($"[mcphost] starting, protocol {Shared.Contracts.McpHost.McpHostProtocol.Version}, pipe '{pipeName}', idle exit {idleExitMs} ms.");

var registry = new McpClientRegistry();
var dispatcher = new RequestDispatcher(registry);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var server = new PipeServer(pipeName, dispatcher, idleExitMs);
try
{
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
}

try
{
    await dispatcher.StopRegistryAsync().WaitAsync(TimeSpan.FromSeconds(3));
}
catch (Exception ex)
{
    Console.Error.WriteLine("[mcphost] registry shutdown error: " + ex.Message);
}

Console.WriteLine("[mcphost] exiting.");
return 0;
