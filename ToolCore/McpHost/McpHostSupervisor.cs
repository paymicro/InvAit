using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Shared.Contracts.McpHost;
using Shared.Ipc;

namespace ToolCore.McpHost;

public enum McpHostConnectionState
{
    Idle,
    Connecting,
    Connected,
    Recovering,
    Disposed,
}

public sealed class McpHostSupervisor : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger _logger;
    private readonly McpHostSupervisorOptions _options;
    private readonly string _pipeName;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<McpHostResponse>> _pending = new();

    private Timer? _watchdogTimer;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _restartCts;
    private Process? _process;
    private NamedPipeClientStream? _pipe;

    private long _nextId;
    private int _pingFailures;
    private int _restartAttempts;
    private volatile McpHostConnectionState _state = McpHostConnectionState.Idle;
    private volatile bool _everConnected;
    private volatile bool _disposed;

    public McpHostSupervisor(McpHostSupervisorOptions options, ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.HostExecutablePath))
            throw new ArgumentException("HostExecutablePath is required.", nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pipeName = string.IsNullOrWhiteSpace(_options.PipeName)
            ? $"invait.mcp.{Guid.NewGuid():N}"
            : _options.PipeName!;
    }

    public event Action<string>? ConnectionLost;

    public bool IsConnected => _state == McpHostConnectionState.Connected && _pipe is { IsConnected: true };

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_state == McpHostConnectionState.Connected)
                return;

            _state = McpHostConnectionState.Connecting;
            await CleanupConnectionAsync().ConfigureAwait(false);

            _restartCts?.Cancel();
            _restartCts?.Dispose();
            _restartCts = null;

            StartProcessCore();

            var pipe = await ConnectPipeAsync(cancellationToken).ConfigureAwait(false);
            _pipe = pipe;

            _connectionCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(pipe, _connectionCts.Token), CancellationToken.None);

            StartWatchdog();

            if (_disposed)
                throw new ObjectDisposedException(nameof(McpHostSupervisor));

            try
            {
                var ping = await SendRequestCoreAsync(McpHostMethods.Ping, null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (!ping.Success)
                    throw new McpHostStartupException($"MCP host handshake failed: {ping.Error}");
                var info = JsonUtils.GetObject<McpHostPingResult>(ping.Result!.Value)
                           ?? throw new McpHostStartupException("MCP host handshake returned an unexpected payload.");
                EnsureProtocolVersion(info.Version);
                Log($"MCP host connected (pid {info.Pid}, protocol {info.Version}).");
            }
            catch (McpHostStartupException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpHostStartupException("MCP host failed to start or did not respond within the start timeout.", ex);
            }

            _restartAttempts = 0;
            Interlocked.Exchange(ref _pingFailures, 0);
            _everConnected = true;
            _state = McpHostConnectionState.Connected;
        }
        catch
        {
            var wasEverConnected = _everConnected;
            _state = wasEverConnected ? McpHostConnectionState.Recovering : McpHostConnectionState.Idle;
            await CleanupConnectionAsync().ConfigureAwait(false);
            if (wasEverConnected && !_disposed)
                ScheduleRestart();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public Task<McpHostResponse> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(McpHostSupervisor));
        if (_state != McpHostConnectionState.Connected || _pipe is not { IsConnected: true })
            throw new InvalidOperationException("MCP host is not connected. Call EnsureStartedAsync first.");

        return SendRequestCoreAsync(method, parameters, timeout, cancellationToken);
    }

    public async Task<McpHostResponse> SendRequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (_state != McpHostConnectionState.Connected || _pipe is not { IsConnected: true })
            throw new InvalidOperationException("MCP host is not connected. Call EnsureStartedAsync first.");

        return await SendRequestCoreAsync(method, parameters, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpHostPingResult> PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync(McpHostMethods.Ping, null, cancellationToken);
        if (!response.Success || response.Result is not { } result)
            throw new IOException($"Ping failed: {response.Error ?? "empty result"}.");
        return JsonUtils.GetObject<McpHostPingResult>(result)
               ?? throw new IOException("Ping returned an unexpected payload.");
    }

    public async ValueTask DisposeAsync()
    {
        var lockTaken = false;
        try
        {
            lockTaken = await _stateLock.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        if (!lockTaken)
        {
            _disposed = true;
            StopWatchdog();
            FailAllPending(new ObjectDisposedException(nameof(McpHostSupervisor)));
            KillProcess("dispose (lock timeout)");
            return;
        }

        try
        {
            if (_disposed)
                return;

            StopWatchdog();

            if (IsConnected)
            {
                try
                {
                    await SendRequestCoreAsync(McpHostMethods.Shutdown, null, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                    Log("MCP host acknowledged shutdown.");
                }
                catch
                {
                }
            }

            _disposed = true;
            _state = McpHostConnectionState.Disposed;
            _restartCts?.Cancel();
            _restartCts?.Dispose();
            _restartCts = null;

            await CleanupConnectionAsync().ConfigureAwait(false);
            FailAllPending(new ObjectDisposedException(nameof(McpHostSupervisor)));

            GC.SuppressFinalize(this);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    internal static void EnsureProtocolVersion(string actual)
    {
        if (!string.Equals(actual, McpHostProtocol.Version, StringComparison.Ordinal))
            throw new McpHostStartupException($"MCP host protocol mismatch: host '{actual}', client '{McpHostProtocol.Version}'.");
    }

    private async Task<McpHostResponse> SendRequestCoreAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(McpHostSupervisor));

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<McpHostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var registration = timeoutCts.Token.Register(
            state => ((TaskCompletionSource<McpHostResponse>)state!).TrySetCanceled(timeoutCts.Token),
            tcs);

        try
        {
            var request = new McpHostRequest { Id = id, Method = method, Params = parameters };
            var bytes = Encoding.UTF8.GetBytes(JsonUtils.SerializeCompact(request));

            await _writeLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                await FrameCodec.WriteFrameAsync(_pipe!, bytes).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP host request '{method}' timed out after {timeout.TotalMilliseconds} ms.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            OnContactLost($"request '{method}' failed: {ex.Message}");
            throw new IOException($"MCP host connection lost while sending '{method}'.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void StartProcessCore()
    {
        var hostPath = Path.GetFullPath(_options.HostExecutablePath!);
        if (!File.Exists(hostPath))
            throw new McpHostStartupException($"MCP host executable was not found: '{hostPath}'.");

        if (!_options.SkipRuntimeCheck && !DotNetRuntimeLocator.IsRuntimeInstalled())
        {
            Log("ERROR: " + DotNetRuntimeLocator.InstallHint);
            throw new McpHostStartupException(DotNetRuntimeLocator.InstallHint);
        }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(hostPath) ?? Environment.CurrentDirectory,
        };

        var arguments = $"--pipe-name \"{_pipeName}\" --idle-exit-ms {_options.HostIdleExitMs}";

        if (hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            if (!DotNetRuntimeLocator.TryGetDotNetExecutable(out var dotnetExe))
                throw new McpHostStartupException(DotNetRuntimeLocator.InstallHint);
            psi.FileName = dotnetExe;
            psi.Arguments = $"\"{hostPath}\" {arguments}";
        }
        else
        {
            psi.FileName = hostPath;
            psi.Arguments = arguments;
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Log($"host: {e.Data}");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Log($"host(err): {e.Data}");
        };
        process.Exited += (_, _) =>
        {
            try
            {
                var exitCode = process.HasExited ? process.ExitCode : -1;
                Log($"MCP host process exited (code {exitCode}).");
            }
            catch (Exception)
            {
                Log("MCP host process exited.");
            }

            OnProcessExited();
        };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new McpHostStartupException($"Failed to start MCP host process '{psi.FileName}'.");
            }
        }
        catch (Exception ex) when (ex is not McpHostStartupException)
        {
            process.Dispose();
            throw new McpHostStartupException($"Failed to start MCP host process '{psi.FileName}': {ex.Message}", ex);
        }

        _process = process;
        if (_disposed)
        {
            try
            {
                process.Kill();
                process.WaitForExit(2000);
            }
            catch
            {
            }

            process.Dispose();
            throw new ObjectDisposedException(nameof(McpHostSupervisor));
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Log($"MCP host process started (pid {process.Id}): {psi.FileName} {arguments}");
    }

    private async Task<NamedPipeClientStream> ConnectPipeAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.StartTimeout;
        while (true)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("Timed out waiting for MCP host pipe.");

                var sliceMs = (int)Math.Min(remaining.TotalMilliseconds, 250);
                var connectTask = Task.Run(() => pipe.Connect(sliceMs), cancellationToken);
                await connectTask.ConfigureAwait(false);
                return pipe;
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
                pipe.Dispose();
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is IOException or System.ComponentModel.Win32Exception) && DateTime.UtcNow < deadline)
            {
                pipe.Dispose();
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var payload = await FrameCodec.ReadFrameAsync(pipe, McpHostProtocol.MaxFrameBytes, cancellationToken).ConfigureAwait(false);
                if (payload == null)
                    break;

                McpHostResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<McpHostResponse>(payload, JsonOptions);
                }
                catch (JsonException ex)
                {
                    Log($"ERROR: Failed to deserialize MCP host frame: {ex.Message}");
                    continue;
                }

                if (response == null)
                    continue;

                if (_pending.TryRemove(response.Id, out var tcs))
                    tcs.TrySetResult(response);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (!_disposed)
        {
            Log($"MCP host read loop terminated: {ex.Message}");
            if (!_disposed && _state == McpHostConnectionState.Connected)
                OnContactLost($"read loop failed: {ex.Message}");
        }

        if (!_disposed && _state == McpHostConnectionState.Connected)
            OnContactLost("host closed the connection stream.");
    }

    private void StartWatchdog()
    {
        StopWatchdog();
        var interval = _options.PingInterval;
        var dueMs = (int)(interval.TotalMilliseconds * 1.5);
        var periodMs = (int)interval.TotalMilliseconds;
        _watchdogTimer = new Timer(_ => { _ = WatchdogTickAsync(); }, null, dueMs, periodMs);
    }

    private void StopWatchdog()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
    }

    private async Task WatchdogTickAsync()
    {
        if (_disposed || _state != McpHostConnectionState.Connected)
            return;

        try
        {
            await SendRequestCoreAsync(McpHostMethods.Ping, null, _options.PingTimeout, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _pingFailures, 0);
        }
        catch (Exception)
        {
            var failures = Interlocked.Increment(ref _pingFailures);
            if (failures >= Math.Max(1, _options.PingFailureThreshold) && !_disposed && _state == McpHostConnectionState.Connected)
                OnContactLost($"{failures} consecutive pings failed.");
        }
    }

    private void OnContactLost(string reason)
    {
        if (_disposed || _state != McpHostConnectionState.Connected)
            return;

        var lockTaken = false;
        string? fireReason = null;
        try
        {
            lockTaken = _stateLock.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (!lockTaken)
            return;

        try
        {
            if (_disposed || _state != McpHostConnectionState.Connected)
                return;

            Log($"MCP host contact lost: {reason}");

            _state = McpHostConnectionState.Recovering;
            StopWatchdog();

            FailAllPending(new IOException("Connection to MCP host lost."));
            _connectionCts?.Cancel();

            KillProcess("contact lost");
            ScheduleRestart();
            fireReason = reason;
        }
        finally
        {
            _stateLock.Release();
        }

        ConnectionLost?.Invoke(fireReason);
    }

    private void OnProcessExited()
    {
        if (_disposed)
            return;

        if (_state == McpHostConnectionState.Connected)
            OnContactLost("host process exited unexpectedly.");
    }

    private void ScheduleRestart()
    {
        if (_disposed || !_everConnected)
            return;

        var attempts = ++_restartAttempts;
        var delay = TimeSpan.FromMilliseconds(Math.Min(
            _options.RestartBaseDelay.TotalMilliseconds * Math.Pow(2, attempts - 1),
            _options.RestartMaxDelay.TotalMilliseconds));

        _restartCts?.Dispose();
        _restartCts = new CancellationTokenSource();
        var token = _restartCts.Token;

        Log($"Scheduling MCP host restart in {delay.TotalMilliseconds} ms (attempt {attempts}).");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                if (_disposed)
                    return;
                Log("Restarting MCP host...");
                await EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (McpHostStartupException ex)
            {
                Log("ERROR: MCP host restart failed: " + ex.UserMessage);
            }
            catch (Exception ex)
            {
                Log($"ERROR: MCP host restart failed: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private async Task CleanupConnectionAsync()
    {
        StopWatchdog();

        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;

        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }
        _pipe = null;

        KillProcess("cleanup");

        _process?.Dispose();
        _process = null;
    }

    private void KillProcess(string reason)
    {
        var process = _process;
        if (process == null)
            return;

        try
        {
            process.Refresh();
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(3000);
                Log($"MCP host process killed ({reason}).");
            }
        }
        catch (Exception ex)
        {
            Log($"WARN: Failed to kill MCP host process ({reason}): {ex.Message}");
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var tcs))
                tcs.TrySetException(exception);
        }
    }

    private void Log(string message) => _logger.Log("[McpHost] " + message);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(McpHostSupervisor));
    }
}
