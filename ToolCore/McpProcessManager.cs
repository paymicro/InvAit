using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ToolCore;

/// <summary>
/// Управление MCP процессами (NPX серверы или dotnet tool например)
/// 
/// TODO перевести на официальный ModelContextProtocol.Core (хотя из-за него ошибка с загрузкой расширения - скорее всего конфликт версий System.Text.Json)
/// Подумать над отдельным процессом-сервером. Который будет на asp .Net 10+ и уже в нем вся работа с MCP.
/// Или уже полностью переходить на VS Extensibility.
/// </summary>
public class McpProcessManager : IDisposable
{
    private static readonly List<string> _copyEnvs = ["PATH", "APPDATA", "LOCALAPPDATA", "TEMP", "SystemRoot", "SystemDrive"];
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<McpNotification>> _notificationQueues = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TaskCompletionSource<ToolResponse>>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _processCancellationTokens = new();
    private readonly object _processLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAccessTime = new();
    private readonly ILogger _logger;
    private CancellationTokenSource _cleanupCts;
    // время жизни MCP процесса без активности
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(10);

    public McpProcessManager(ILogger logger)
    {
        _logger = logger ?? new NullLogger();
    }

    /// <summary>
    /// Запустить фоновую задачу очистки неактивных процессов
    /// </summary>
    private void StartCleanupTask()
    {
        // Use lock to prevent race condition when multiple threads call StartCleanupTask
        lock (_processLock)
        {
            if (_cleanupCts != null) return;
            _cleanupCts = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cleanupCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, _cleanupCts.Token);

                    var now = DateTime.UtcNow;
                    var serverIds = _lastAccessTime.Keys.ToList();

                    foreach (var serverId in serverIds)
                    {
                        if (_lastAccessTime.TryGetValue(serverId, out var lastAccess))
                        {
                            if (now - lastAccess > _idleTimeout)
                            {
                                _logger.Log($"MCP [{serverId}] idle timeout reached, stopping process");
                                await StopProcessAsync(serverId);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                _logger.Log($"MCP cleanup task error: {ex.Message}", "ERROR");
            }
        }, _cleanupCts.Token);
    }

    /// <summary>
    /// Обновить время последнего доступа к процессу
    /// </summary>
    private void UpdateLastAccess(string serverId)
    {
        _lastAccessTime[serverId] = DateTime.UtcNow;
    }

    private async Task<string> GetFullPathCommandAsync(string commandName)
    {
        // Получаем список расширений из PATHEXT (.COM;.EXE;.BAT;.CMD и т.д.)
        var extensions = Environment.GetEnvironmentVariable("PATHEXT")?.Split(';')
            ?? [".exe", ".com", ".bat", ".cmd"]; // fallback

        // Получаем все пути из PATH
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        // Добавляем текущую директорию в начало поиска (стандартное поведение Windows)
        var searchPaths = new[] { Directory.GetCurrentDirectory() }.Concat(paths);

        var fileNameExt = Path.GetExtension(commandName).ToUpperInvariant();
        var hasExecutableExtension = !string.IsNullOrEmpty(fileNameExt) && extensions.Contains(fileNameExt);

        foreach (var directory in searchPaths)
        {
            var fullPathWithOriginalName = Path.Combine(directory, commandName);

            // Если пользователь УЖЕ указал исполняемое расширение (напр. npx.cmd)
            // или если файла npx.exe не существует, но есть какой-то специфичный файл,
            // который мы считаем запускаемым "как есть" (редкий случай для Windows).
            if (hasExecutableExtension && File.Exists(fullPathWithOriginalName))
            {
                return fullPathWithOriginalName;
            }

            // Если расширения нет (npx) или оно не входит в PATHEXT,
            // перебираем все возможные варианты из PATHEXT
            foreach (var ext in extensions)
            {
                // Если файл уже имеет расширение (напр. npx.sh), 
                // Path.ChangeExtension заменит .sh на .exe, .cmd и т.д.
                // Если расширения нет, он просто добавит его.
                var candidatePath = Path.ChangeExtension(fullPathWithOriginalName, ext);

                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Запустить MCP процесс
    /// </summary>
    public async Task<string> StartProcessAsync(string serverId, string command, string args, string workingDirectory, Dictionary<string, string>? env)
    {
        lock (_processLock)
        {
            if (_processes.TryGetValue(serverId, out var proc))
            {
                if (!proc.HasExited)
                {
                    return $"OK: Process {serverId} already running.";
                }
                else
                {
                    // Process exited unexpectedly, clean up
                    _processes.TryRemove(serverId, out _);
                    _notificationQueues.TryRemove(serverId, out _);
                    _pendingRequests.TryRemove(serverId, out _);
                    proc.Dispose();
                }
            }
        }

        try
        {
            var fullCommand = await GetFullPathCommandAsync(command);
            if (string.IsNullOrEmpty(fullCommand))
            {
                var message = $"ERROR: Failed to find {command} in system.";
                _logger.Log(message, "ERROR");
                return message;
            }

            _logger.Log($"MCP {command} > {fullCommand}");
            var startInfo = new ProcessStartInfo
            {
                FileName = fullCommand,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };

            SetEnvironments(env, startInfo);

            var process = Process.Start(startInfo);
            if (process == null)
            {
                return "ERROR: Failed to start process";
            }

            process.EnableRaisingEvents = true;
            process.Exited += (s, e) =>
            {
                // Cancel all reading tasks for this process
                if (_processCancellationTokens.TryRemove(serverId, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }

                _processes.TryRemove(serverId, out _);
                _notificationQueues.TryRemove(serverId, out _);
                if (_pendingRequests.TryRemove(serverId, out var requests))
                {
                    foreach (var tcs in requests.Values)
                    {
                        tcs.TrySetCanceled();
                    }
                }
            };

            lock (_processLock)
            {
                _processes[serverId] = process;
            }
            _notificationQueues[serverId] = new ConcurrentQueue<McpNotification>();
            _pendingRequests[serverId] = new ConcurrentDictionary<string, TaskCompletionSource<ToolResponse>>();

            // Create CancellationTokenSource for process (linked with cleanup token)
            // Ensure _cleanupCts is initialized before linking
            StartCleanupTask();
            var cleanupToken = _cleanupCts?.Token ?? CancellationToken.None;
            var processCts = CancellationTokenSource.CreateLinkedTokenSource(cleanupToken);
            _processCancellationTokens[serverId] = processCts;
            var cancellationToken = processCts.Token;

            UpdateLastAccess(serverId);

            // Асинхронное чтение stdout с поддержкой отмены
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.HasExited && !process.StandardOutput.EndOfStream && !cancellationToken.IsCancellationRequested)
                    {
                        // Используем Task.WhenAny для возможности отмены ReadLineAsync
                        var readTask = process.StandardOutput.ReadLineAsync();
                        var completedTask = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, cancellationToken));

                        if (cancellationToken.IsCancellationRequested)
                            break;

                        if (completedTask == readTask)
                        {
                            var line = await readTask;
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            _logger.Log($"MCP [{serverId}] received: {line}");

                            try
                            {
                                // Это ответ?
                                var response = JsonUtils.Deserialize<McpResponse>(line);
                                if (response is { Id: not null })
                                {
                                    if (_pendingRequests.TryGetValue(serverId, out var serverRequests)
                                        && serverRequests.TryRemove(response.Id, out var tcs))
                                    {
                                        if (response.Error == null)
                                        {
                                            tcs.SetResult(new ToolResponse
                                            {
                                                Payload = JsonUtils.Serialize(response.Result),
                                            });
                                        }
                                        else
                                        {
                                            tcs.SetResult(new ToolResponse
                                            {
                                                Success = false,
                                                Payload = JsonUtils.Serialize(response.Error),
                                            });
                                        }
                                        continue;
                                    }
                                }

                                // Это уведомление?
                                var notification = JsonUtils.Deserialize<McpNotification>(line);
                                if (notification != null)
                                {
                                    _notificationQueues[serverId].Enqueue(notification);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Log($"MCP [{serverId}] parse error: {ex.Message}. Line: {line}", "WARN");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.Log($"MCP [{serverId}] stdout error: {ex.Message}", "ERROR");
                    }
                }
            }, cancellationToken);

            // Асинхронное чтение stderr для логирования с поддержкой отмены
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.HasExited && !process.StandardError.EndOfStream && !cancellationToken.IsCancellationRequested)
                    {
                        // Используем Task.WhenAny для возможности отмены ReadLineAsync
                        var readTask = process.StandardError.ReadLineAsync();
                        var completedTask = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, cancellationToken));

                        if (cancellationToken.IsCancellationRequested)
                            break;

                        if (completedTask == readTask)
                        {
                            var line = await readTask;
                            if (line != null)
                            {
                                _logger.Log($"MCP [{serverId}] stderr: {line}", "INFO");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.Log($"MCP [{serverId}] stderr error: {ex.Message}", "ERROR");
                    }
                }
            }, cancellationToken);

            _logger.Log($"MCP process started: {serverId} ({command} {args})");
            return $"OK: Process started: {serverId}";
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to start MCP process {serverId}: {ex.Message}", "ERROR");
            return $"ERROR: {ex.Message}";
        }
    }

    private static void SetEnvironments(Dictionary<string, string>? env, ProcessStartInfo startInfo)
    {
        // Полная очистка унаследованных переменных
        startInfo.EnvironmentVariables.Clear();

        // Добавление системного минимума
        foreach (var copyEnv in _copyEnvs)
        {
            startInfo.EnvironmentVariables[copyEnv] = Environment.GetEnvironmentVariable(copyEnv);
        }

        // Добавление переменных окружения
        if (env != null)
        {
            foreach (var kvp in env)
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// Остановить MCP процесс
    /// </summary>
    public async Task<string> StopProcessAsync(string serverId)
    {
        _lastAccessTime.TryRemove(serverId, out _);

        // Cancel reading tasks
        if (_processCancellationTokens.TryRemove(serverId, out var processCts))
        {
            processCts.Cancel();
            processCts.Dispose();
        }

        if (!_processes.TryRemove(serverId, out var process))
        {
            return $"ERROR: Process {serverId} not found";
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                await Task.Run(() => process.WaitForExit(5000));
            }

            process.Dispose();
            _notificationQueues.TryRemove(serverId, out _);
            _pendingRequests.TryRemove(serverId, out var requests);

            if (requests != null)
            {
                foreach (var tcs in requests.Values)
                {
                    tcs.TrySetCanceled();
                }
            }

            _logger.Log($"MCP process stopped: {serverId}");
            return $"OK: Process stopped: {serverId}";
        }
        catch (Exception ex)
        {
            _logger.Log($"Error stopping MCP process {serverId}: {ex.Message}", "ERROR");
            return $"ERROR: {ex.Message}";
        }
    }

    /// <summary>
    /// Отправить сообщение и ждать ответа
    /// </summary>
    public async Task<ToolResponse> CallMethodAsync(string serverId, string id, string message, int timeoutMs = 10000)
    {
        if (!_processes.TryGetValue(serverId, out var process))
        {
            return new ToolResponse { Success = false, Error = "ERROR: Process not found" };
        }

        UpdateLastAccess(serverId);

        var tcs = new TaskCompletionSource<ToolResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryGetValue(serverId, out var serverRequests))
        {
            return new ToolResponse
            {
                Success = false,
                Error = "ERROR: Server requests dictionary not initialized"
            };
        }

        serverRequests[id] = tcs;

        try
        {
            await process.StandardInput.WriteLineAsync(message);
            await process.StandardInput.FlushAsync();
            _logger.Log($"MCP [{serverId}] sent: {message}");

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask == tcs.Task)
            {
                return await tcs.Task;
            }
            else
            {
                serverRequests.TryRemove(id, out _);
                return new ToolResponse
                {
                    Success = false,
                    Error = "ERROR: Timeout waiting for response"
                };
            }
        }
        catch (Exception ex)
        {
            serverRequests.TryRemove(id, out _);
            _logger.Log($"Error calling MCP method in {serverId}: {ex.Message}", "ERROR");
            return new ToolResponse
            {
                Success = false,
                Error = $"ERROR: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Send message to process stdin (without waiting for response)
    /// </summary>
    public async Task<string> SendMessageAsync(string serverId, string message)
    {
        if (!_processes.TryGetValue(serverId, out var process))
        {
            return "ERROR: Process not found";
        }

        UpdateLastAccess(serverId);

        try
        {
            await process.StandardInput.WriteLineAsync(message);
            await process.StandardInput.FlushAsync();
            _logger.Log($"MCP [{serverId}] sent raw: {message}");
            return "OK";
        }
        catch (Exception ex)
        {
            _logger.Log($"Error sending to MCP process {serverId}: {ex.Message}", "ERROR");
            return $"ERROR: {ex.Message}";
        }
    }

    /// <summary>
    /// Проверить, запущен ли процесс
    /// </summary>
    public bool IsProcessRunning(string serverId)
    {
        if (_processes.TryGetValue(serverId, out var process))
        {
            return !process.HasExited;
        }
        return false;
    }

    /// <summary>
    /// Остановить все процессы
    /// </summary>
    public async Task StopAllProcessesAsync()
    {
        var serverIds = _processes.Keys.ToArray();
        foreach (var serverId in serverIds)
        {
            await StopProcessAsync(serverId);
        }
    }

    /// <summary>
    /// Get list of running server IDs
    /// </summary>
    public IEnumerable<string> GetRunningServers()
    {
        return _processes.Keys;
    }

    public void Dispose()
    {
        _cleanupCts?.Cancel();
        _cleanupCts?.Dispose();
        var stopTask = StopAllProcessesAsync();
        try
        {
            stopTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // игнорируем
        }
    }

    /// <summary>
    /// Null logger implementation for cases when no logger is provided
    /// </summary>
    private class NullLogger : ILogger
    {
        public void Log(string message, string level = "INFO")
        {
            // No-op
        }
    }
}
