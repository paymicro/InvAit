using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using InvAit.Utils;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Shared.Contracts;
using Shared.Contracts.Mcp;

namespace InvAit.Agent;

/// <summary>
/// Управление MCP процессами (NPX серверы или dotnet tool например)
/// 
/// TODO перевести на официальный ModelContextProtocol.Core (хотя из-за него ошибка с загрузкой расширения - скорее всего конфликт версий System.Text.Json)
/// Подумать над отдельным процессом-сервером. Который будет на asp .Net 10+ и уже в нем вся работа с MCP.
/// Или уже полностью переходить на VS Extensibility.
/// </summary>
public class McpProcessManager : IDisposable
{
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<McpNotification>> _notificationQueues = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TaskCompletionSource<VsResponse>>> _pendingRequests = new();
    private readonly object _processLock = new();

    private async Task<string> GetFullPathCommandAsync(string commandName)
    {
        var tcs = new TaskCompletionSource<string>();

        var startInfo = new ProcessStartInfo
        {
            FileName = "where",
            Arguments = commandName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // Read output before process exits to avoid deadlock
        var outputBuilder = new StringBuilder();
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.Exited += (sender, args) =>
        {
            try
            {
                if (process.ExitCode == 0)
                {
                    var rawOutput = outputBuilder.ToString();
                    var paths = rawOutput.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);

                    // Filter for executable extensions (.cmd, .bat, .exe)
                    var executablePath = paths.FirstOrDefault(p =>
                        p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

                    tcs.TrySetResult(executablePath?.Trim());
                }
                else
                {
                    // Command not found or error
                    tcs.TrySetResult(null);
                }
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        };

        try
        {
            if (process.Start())
            {
                process.BeginOutputReadLine();
            }
            else
            {
                tcs.TrySetResult(null);
            }
        }
        catch
        {
            tcs.TrySetResult(null);
        }

        return await tcs.Task;
    }

    /// <summary>
    /// Запустить MCP процесс
    /// </summary>
    public async Task<string> StartProcessAsync(string serverId, string command, string args, string workingDirectory, Dictionary<string, string> env)
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
                var message = $"ERROR: Failed to get where {command} in system.";
                await Logger.LogAsync(message, "ERROR");
                return message;
            }

            await Logger.LogAsync($"MCP {command} > {fullCommand}");
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
            _pendingRequests[serverId] = new ConcurrentDictionary<string, TaskCompletionSource<VsResponse>>();

            // Асинхронное чтение stdout
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.HasExited && !process.StandardOutput.EndOfStream)
                    {
                        var line = await process.StandardOutput.ReadLineAsync();
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        await Logger.LogAsync($"MCP [{serverId}] received: {line}");

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
                                        tcs.SetResult(new VsResponse
                                        {
                                            Payload = JsonUtils.Serialize(response.Result),
                                        });
                                    }
                                    else
                                    {
                                        tcs.SetResult(new VsResponse
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
                            await Logger.LogAsync($"MCP [{serverId}] parse error: {ex.Message}. Line: {line}", "WARN");
                        }

                        await Task.Delay(200);
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
                    while (!process.HasExited && !process.StandardError.EndOfStream)
                    {
                        var line = await process.StandardError.ReadLineAsync();
                        if (line != null)
                        {
                            await Logger.LogAsync($"MCP [{serverId}] stderr: {line}", "INFO");
                        }
                        await Task.Delay(200);
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
        startInfo.EnvironmentVariables["APPDATA"] = Environment.GetEnvironmentVariable("APPDATA");
        startInfo.EnvironmentVariables["LOCALAPPDATA"] = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        startInfo.EnvironmentVariables["TEMP"] = Environment.GetEnvironmentVariable("TEMP");
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
    public async Task<VsResponse> CallMethodAsync(string serverId, string id, string message, int timeoutMs = 10000)
    {
        if (!_processes.TryGetValue(serverId, out var process))
        {
            return new VsResponse { Success = false, Error = "ERROR: Process not found" };
        }

        var tcs = new TaskCompletionSource<VsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryGetValue(serverId, out var serverRequests))
        {
            return new VsResponse
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
            await Logger.LogAsync($"MCP [{serverId}] sent: {message}");

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completedTask == tcs.Task)
            {
                return await tcs.Task;
            }
            else
            {
                serverRequests.TryRemove(id, out _);
                return new VsResponse
                {
                    Success = false,
                    Error = "ERROR: Timeout waiting for response"
                };
            }
        }
        catch (Exception ex)
        {
            serverRequests.TryRemove(id, out _);
            await Logger.LogAsync($"Error calling MCP method in {serverId}: {ex.Message}", "ERROR");
            return new VsResponse
            {
                Success = false,
                Error = $"ERROR: {ex.Message}"
            };
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

    public void Dispose()
    {
        StopAllProcessesAsync().FileAndForget("ChatToolWindow.DisposeAll");
    }
}
