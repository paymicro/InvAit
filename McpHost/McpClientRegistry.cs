using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Encodings.Web;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shared.Contracts.McpHost;

namespace McpHost;

public sealed class McpClientRegistry : IAsyncDisposable
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class ManagedServer
    {
        public required string ServerId { get; init; }
        public required McpClient Client { get; init; }
        public required string Fingerprint { get; init; }
        public long LastUsedTimestamp { get; set; } = Stopwatch.GetTimestamp();
        public void Touch() => LastUsedTimestamp = Stopwatch.GetTimestamp();
        public TimeSpan IdleFor => Stopwatch.GetElapsedTime(LastUsedTimestamp);
    }

    private readonly ConcurrentDictionary<string, ManagedServer> _servers = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly System.Threading.Timer _reaperTimer;

    public McpClientRegistry()
    {
        _reaperTimer = new Timer(_ => { _ = ReapIdleServersAsync(); }, null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task<JsonElement> ListToolsAsync(McpListToolsParams parameters, CancellationToken cancellationToken)
    {
        return await WithRecoveryAsync(parameters, async (client, ct) =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
            var tools = await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, timeoutCts.Token);

            return JsonSerializer.SerializeToElement(new
            {
                tools = tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = t.JsonSchema,
                }),
            }, JsonOptions);
        }, cancellationToken);
    }

    public async Task<JsonElement> CallToolAsync(McpCallToolParams parameters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parameters.ToolName))
            throw new ArgumentException("toolName is required.");

        var arguments = new Dictionary<string, object?>();
        if (parameters.Arguments is { ValueKind: JsonValueKind.Object } argsElement)
        {
            foreach (var property in argsElement.EnumerateObject())
                arguments[property.Name] = property.Value.Clone();
        }

        return await WithRecoveryAsync(parameters, async (client, ct) =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Math.Max(1000, parameters.TimeoutMs));

            CallToolResult result;
            try
            {
                result = await client.CallToolAsync(parameters.ToolName, arguments,
                    null, null, timeoutCts.Token);
            }
            catch (McpProtocolException ex)
            {
                return JsonSerializer.SerializeToElement(new
                {
                    content = new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = ex.Message } },
                    isError = true,
                }, JsonOptions);
            }

            return SerializeToolResult(result);
        }, cancellationToken);
    }

    /// <summary>
    /// Runs the action against the lazily-started client. If the transport turns out to be dead
    /// (e.g. the server process was killed externally), drops the cached entry, starts a fresh
    /// one and retries exactly once; the second failure propagates to the caller.
    /// </summary>
    private async Task<T> WithRecoveryAsync<T>(McpServerLaunchInfo parameters, Func<McpClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var client = await GetOrStartAsync(parameters, cancellationToken);
        try
        {
            return await action(client, cancellationToken);
        }
        catch (Exception ex) when (IsTransportDeath(ex))
        {
            Log($"MCP server '{parameters.ServerId}' is unreachable ({ex.Message}), restarting it.");
            await StopServerAsync(parameters.ServerId);
            var fresh = await GetOrStartAsync(parameters, cancellationToken);
            return await action(fresh, cancellationToken);
        }
    }

    private static bool IsTransportDeath(Exception ex)
    {
        if (ex is OperationCanceledException or ArgumentException or McpProtocolException)
            return false;

        if (ex is IOException or ObjectDisposedException or HttpRequestException or System.Net.Sockets.SocketException)
            return true;

        return ex.Message?.Contains("exited unexpectedly", StringComparison.OrdinalIgnoreCase) == true;
    }

    public async Task<bool> StopServerAsync(string serverId)
    {
        if (!_servers.TryRemove(serverId, out var managed))
            return false;

        await DisposeClientAsync(managed.ServerId, managed.Client);
        return true;
    }

    public async Task<int> StopAllAsync()
    {
        var ids = _servers.Keys.ToList();
        var stopped = 0;
        foreach (var id in ids)
        {
            if (await StopServerAsync(id))
                stopped++;
        }

        return stopped;
    }

    public IReadOnlyList<object> DescribeServers()
    {
        return _servers.Select(kv => (object)new
        {
            id = kv.Key,
            idleMs = (long)kv.Value.IdleFor.TotalMilliseconds,
        }).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        _reaperTimer.Dispose();
        await StopAllAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<McpClient> GetOrStartAsync(McpServerLaunchInfo parameters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parameters.ServerId))
            throw new ArgumentException("serverId is required.");
        if (string.IsNullOrWhiteSpace(parameters.Command) && string.IsNullOrWhiteSpace(parameters.Url))
            throw new ArgumentException($"command or url is required to start MCP server '{parameters.ServerId}'.");

        var fingerprint = BuildFingerprint(parameters);

        if (_servers.TryGetValue(parameters.ServerId, out var existing) && existing.Fingerprint == fingerprint)
        {
            existing.Touch();
            return existing.Client;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_servers.TryGetValue(parameters.ServerId, out existing))
            {
                if (existing.Fingerprint == fingerprint)
                {
                    existing.Touch();
                    return existing.Client;
                }

                Log($"MCP server '{parameters.ServerId}' launch parameters changed, restarting it.");
                await StopServerAsync(parameters.ServerId);
            }

            var client = await StartAsync(parameters, cancellationToken);
            _servers[parameters.ServerId] = new ManagedServer
            {
                ServerId = parameters.ServerId,
                Client = client,
                Fingerprint = fingerprint,
            };
            Log($"MCP server '{parameters.ServerId}' started.");
            return client;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public static string BuildFingerprint(McpServerLaunchInfo parameters)
    {
        var env = parameters.Env == null
            ? string.Empty
            : string.Join(";", parameters.Env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}"));
        var headers = string.IsNullOrEmpty(parameters.Url)
            ? string.Empty
            : parameters.Headers == null
                ? string.Empty
                : string.Join(";", parameters.Headers.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key.ToLowerInvariant()}={kv.Value}"));
        return string.Join("|",
            parameters.Url ?? string.Empty,
            parameters.Command ?? string.Empty,
            string.Join(" ", parameters.Args ?? []),
            parameters.WorkingDirectory ?? string.Empty,
            env,
            headers);
    }

    private async Task<McpClient> StartAsync(McpServerLaunchInfo parameters, CancellationToken cancellationToken)
    {
        IClientTransport transport;
        if (!string.IsNullOrWhiteSpace(parameters.Url))
        {
            var httpOptions = new HttpClientTransportOptions
            {
                Name = parameters.ServerId,
                Endpoint = new Uri(parameters.Url),
            };

            if (parameters.Headers is { Count: > 0 })
                httpOptions.AdditionalHeaders = parameters.Headers;

            transport = new HttpClientTransport(httpOptions);
        }
        else
        {
            transport = CreateStdioTransport(parameters);
        }

        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to start or initialize MCP server '{parameters.ServerId}': {ex.Message}", ex);
        }
    }

    private static IClientTransport CreateStdioTransport(McpServerLaunchInfo parameters)
    {
        var command = ResolveCommand(parameters.Command!);
        if (string.IsNullOrEmpty(command))
            throw new FileNotFoundException($"Failed to find '{parameters.Command}' in system.");

        var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        if (parameters.Env != null)
        {
            foreach (var pair in parameters.Env)
                env[pair.Key] = pair.Value;
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = parameters.ServerId,
            Command = command,
            Arguments = parameters.Args ?? [],
            WorkingDirectory = string.IsNullOrWhiteSpace(parameters.WorkingDirectory) ? null : parameters.WorkingDirectory,
            EnvironmentVariables = env,
            InheritEnvironmentVariables = false,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                    Console.Error.WriteLine($"[mcp:{parameters.ServerId}] {line}");
            },
        });
    }

    internal static string? ResolveCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        if (Path.IsPathRooted(command))
            return File.Exists(command) ? command : null;

        var isWindows = OperatingSystem.IsWindows();
        var extensions = isWindows && !command.Contains('.')
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(isWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, command + (extension.Length > 0 && !extension.StartsWith('.') ? "." + extension : extension));
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private async Task ReapIdleServersAsync()
    {
        try
        {
            foreach (var kv in _servers)
            {
                if (!_servers.TryRemove(kv.Key, out var managed))
                    continue;

                if (managed.IdleFor < IdleLifetime)
                {
                    if (!_servers.TryAdd(kv.Key, managed))
                        await DisposeClientAsync(managed.ServerId, managed.Client);
                    continue;
                }

                Log($"MCP server '{kv.Key}' idle for {managed.IdleFor.TotalMinutes:F1} min, stopping it.");
                await DisposeClientAsync(managed.ServerId, managed.Client);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR: idle reaper failed: " + ex.Message);
        }
    }

    private async Task DisposeClientAsync(string serverId, McpClient client)
    {
        try
        {
            await client.DisposeAsync();
            Log($"MCP server '{serverId}' stopped.");
        }
        catch (Exception ex)
        {
            Log($"WARN: error while stopping MCP server '{serverId}': {ex.Message}");
        }
    }

    private static JsonElement SerializeToolResult(CallToolResult result)
    {
        var content = new List<object>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text:
                    content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ImageContentBlock image:
                    content.Add(new Dictionary<string, object?> { ["type"] = "image", ["data"] = Convert.ToBase64String(image.Data.ToArray()), ["mimeType"] = image.MimeType });
                    break;
                case AudioContentBlock audio:
                    content.Add(new Dictionary<string, object?> { ["type"] = "audio", ["data"] = Convert.ToBase64String(audio.Data.ToArray()), ["mimeType"] = audio.MimeType });
                    break;
                default:
                    content.Add(JsonSerializer.SerializeToElement(block, JsonOptions));
                    break;
            }
        }

        return JsonSerializer.SerializeToElement(new
        {
            content,
            isError = result.IsError ?? false,
        }, JsonOptions);
    }

    private void Log(string message) => Console.Out.WriteLine("[registry] " + message);
}
