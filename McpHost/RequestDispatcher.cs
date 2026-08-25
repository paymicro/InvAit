using System.Diagnostics;
using System.Text.Json;
using Shared.Contracts.McpHost;

namespace McpHost;

public sealed class RequestDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly long _startedAtTimestamp = Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource _stopTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly McpClientRegistry _registry;

    public bool StopRequested { get; private set; }

    public Task WaitStoppedAsync() => _stopTcs.Task;

    public RequestDispatcher(McpClientRegistry registry) => _registry = registry;

    public async Task<McpHostResponse> HandleAsync(McpHostRequest request)
    {
        try
        {
            return request.Method switch
            {
                McpHostMethods.Ping => Success(request.Id, JsonSerializer.SerializeToElement(new
                {
                    version = McpHostProtocol.Version,
                    pid = Environment.ProcessId,
                    uptimeMs = UptimeMs(),
                }, JsonOptions)),
                McpHostMethods.Status => Success(request.Id, JsonSerializer.SerializeToElement(new
                {
                    uptimeMs = UptimeMs(),
                    servers = _registry.DescribeServers(),
                }, JsonOptions)),
                McpHostMethods.ListTools => Success(request.Id, await _registry.ListToolsAsync(GetParams<McpListToolsParams>(request), CancellationToken.None)),
                McpHostMethods.CallTool => Success(request.Id, await _registry.CallToolAsync(GetParams<McpCallToolParams>(request), CancellationToken.None)),
                McpHostMethods.StopServer => Success(request.Id, ToElement(await _registry.StopServerAsync(GetParams<McpStopServerParams>(request).ServerId))),
                McpHostMethods.StopAll => Success(request.Id, ToElement(await _registry.StopAllAsync())),
                McpHostMethods.Shutdown => Success(request.Id, null),
                _ => Failure(request.Id, $"Unknown method '{request.Method}'."),
            };
        }
        catch (Exception ex)
        {
            return Failure(request.Id, ex.Message);
        }
    }

    public void MarkStopRequested()
    {
        StopRequested = true;
        _stopTcs.TrySetResult();
    }

    public async Task StopRegistryAsync() => await _registry.DisposeAsync();

    private static T GetParams<T>(McpHostRequest request) where T : class, new()
        => request.Params is JsonElement element && element.ValueKind == JsonValueKind.Object
            ? element.Deserialize<T>(JsonOptions) ?? new T()
            : new T();

    private static JsonElement? ToElement(object? value)
        => value == null ? null : JsonSerializer.SerializeToElement(value, JsonOptions);

    private long UptimeMs()
        => (long)((Stopwatch.GetTimestamp() - _startedAtTimestamp) * 1000d / Stopwatch.Frequency);

    private static McpHostResponse Success(long id, object? result)
        => new() { Id = id, Success = true, Result = result as JsonElement? };

    private static McpHostResponse Failure(long id, string error)
        => new() { Id = id, Success = false, Error = error };
}
