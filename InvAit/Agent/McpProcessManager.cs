using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using InvAit.Utils;
using Shared.Contracts.Mcp;

namespace InvAit.Agent;

/// <summary>
/// Управление MCP процессами (NPX серверы)
/// </summary>
public class McpProcessManager
{
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<McpNotification>> _notificationQueues = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TaskCompletionSource<string>>> _pendingRequests = new();

    /// <summary>
    /// Запустить MCP процесс
    /// </summary>
    public async Task<string> StartProcessAsync(string serverId, string command, string args, string workingDirectory = null, Dictionary<string, string> env = null)
    {
        if (_processes.TryGetValue(serverId, out var proc))
        {
            if (!proc.HasExited)
            {
                return $"OK: Process {serverId} already running.";
            }
            else
            {
                await Logger.LogAsync($"MCP [{serverId}] unexpected exited.", "WARNING");
                _processes.TryRemove(serverId, out var _);
            }
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{command}\" {args}",
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

            _processes[serverId] = process;
            process.Exited += (s, e) => { _processes.TryRemove(serverId, out var _); };
            _notificationQueues[serverId] = new ConcurrentQueue<McpNotification>();
            _pendingRequests[serverId] = new ConcurrentDictionary<string, TaskCompletionSource<string>>();

            // Асинхронное чтение stdout
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardOutput.EndOfStream)
                    {
                        var line = await process.StandardOutput.ReadLineAsync();
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        await Logger.LogAsync($"MCP [{serverId}] received: {line}");

                        try
                        {
                            // Пытаемся распарсить как сообщение JSON-RPC
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;

                            // Это ответ? (содержит id и result или error)
                            if (root.TryGetProperty("id", out var idProp))
                            {
                                var id = idProp.ValueKind == JsonValueKind.Number
                                    ? idProp.GetInt32().ToString()
                                    : idProp.GetString();

                                if (id != null && _pendingRequests.TryGetValue(serverId, out var serverRequests) &&
                                    serverRequests.TryRemove(id, out var tcs))
                                {
                                    tcs.SetResult(line);
                                    continue;
                                }
                            }

                            // Это уведомление? (содержит method, нет id)
                            if (root.TryGetProperty("method", out var methodProp) && !root.TryGetProperty("id", out _))
                            {
                                var method = methodProp.GetString();
                                if (method != null)
                                {
                                    var notification = JsonUtils.Deserialize<McpNotification>(line);
                                    if (notification != null)
                                    {
                                        _notificationQueues[serverId].Enqueue(notification);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            await Logger.LogAsync($"MCP [{serverId}] parse error: {ex.Message}. Line: {line}", "WARN");
                        }
                    }
                }
                catch (Exception ex)
                {
                    await Logger.LogAsync($"MCP [{serverId}] stdout error: {ex.Message}", "ERROR");
                }
            });

            // Асинхронное чтение stderr для логирования
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardError.EndOfStream)
                    {
                        var line = await process.StandardError.ReadLineAsync();
                        if (line != null)
                        {
                            await Logger.LogAsync($"MCP [{serverId}] stderr: {line}", "INFO");
                        }
                    }
                }
                catch (Exception ex)
                {
                    await Logger.LogAsync($"MCP [{serverId}] stderr error: {ex.Message}", "ERROR");
                }
            });

            await Logger.LogAsync($"MCP process started: {serverId} ({command} {args})");
            return $"OK: Process started: {serverId}";
        }
        catch (Exception ex)
        {
            await Logger.LogAsync($"Failed to start MCP process {serverId}: {ex.Message}", "ERROR");
            return $"ERROR: {ex.Message}";
        }
    }

    private static void SetEnvironments(Dictionary<string, string> env, ProcessStartInfo startInfo)
    {
        // Полная очистка унаследованных переменных
        startInfo.EnvironmentVariables.Clear();

        // Добавление системного минимума
        startInfo.EnvironmentVariables["PATH"] = Environment.GetEnvironmentVariable("PATH");
        startInfo.EnvironmentVariables["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot");
        startInfo.EnvironmentVariables["SystemDrive"] = Environment.GetEnvironmentVariable("SystemDrive");

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

            await Logger.LogAsync($"MCP process stopped: {serverId}");
            return $"OK: Process stopped: {serverId}";
        }
        catch (Exception ex)
        {
            await Logger.LogAsync($"Error stopping MCP process {serverId}: {ex.Message}", "ERROR");
            return $"ERROR: {ex.Message}";
        }
    }

    /// <summary>
    /// Отправить сообщение и ждать ответа
    /// </summary>
    public async Task<string> CallMethodAsync(string serverId, string id, string message, int timeoutMs = 10000)
    {
        if (!_processes.TryGetValue(serverId, out var process))
        {
            return "ERROR: Process not found";
        }

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryGetValue(serverId, out var serverRequests))
        {
            return "ERROR: Server requests dictionary not initialized";
        }

        serverRequests[id] = tcs;

        try
        {
            await process.StandardInput.WriteLineAsync(message);
            await process.StandardInput.FlushAsync();
            await Logger.LogAsync($"MCP [{serverId}] sent: {message}");

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask == tcs.Task)
            {
                return await tcs.Task;
            }
            else
            {
                serverRequests.TryRemove(id, out _);
                return "ERROR: Timeout waiting for response";
            }
        }
        catch (Exception ex)
        {
            serverRequests.TryRemove(id, out _);
            await Logger.LogAsync($"Error calling MCP method in {serverId}: {ex.Message}", "ERROR");
            return $"ERROR: {ex.Message}";
        }
    }

    /// <summary>
    /// Отправить сообщение в stdin процесса (без ожидания ответа)
    /// </summary>
    public async Task<string> SendMessageAsync(string serverId, string message)
    {
        if (!_processes.TryGetValue(serverId, out var process))
        {
            return "ERROR: Process not found";
        }

        try
        {
            await process.StandardInput.WriteLineAsync(message);
            await process.StandardInput.FlushAsync();
            await Logger.LogAsync($"MCP [{serverId}] sent raw: {message}");
            return "OK";
        }
        catch (Exception ex)
        {
            await Logger.LogAsync($"Error sending to MCP process {serverId}: {ex.Message}", "ERROR");
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
}
