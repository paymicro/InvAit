using System;
using System.Collections.Concurrent;
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
    public async Task<string> StartProcessAsync(string serverId, string command, string args, string workingDirectory = null)
    {
        if (_processes.ContainsKey(serverId))
        {
            return $"OK: Process {serverId} already running";
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
            
            var process = Process.Start(startInfo);
            if (process == null)
            {
                return "ERROR: Failed to start process";
            }
            
            _processes[serverId] = process;
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

                        Logger.Log($"MCP [{serverId}] received: {line}");

                        try
                        {
                            // Пытаемся распарсить как сообщение JSON-RPC
                            using (var doc = JsonDocument.Parse(line))
                            {
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
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"MCP [{serverId}] parse error: {ex.Message}. Line: {line}", "WARN");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"MCP [{serverId}] stdout error: {ex.Message}", "ERROR");
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
                            Logger.Log($"MCP [{serverId}] stderr: {line}", "INFO");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"MCP [{serverId}] stderr error: {ex.Message}", "ERROR");
                }
            });
            
            Logger.Log($"MCP process started: {serverId} ({command} {args})");
            return $"OK: Process started: {serverId}";
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to start MCP process {serverId}: {ex.Message}", "ERROR");
            return $"ERROR: {ex.Message}";
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
            
            Logger.Log($"MCP process stopped: {serverId}");
            return $"OK: Process stopped: {serverId}";
        }
        catch (Exception ex)
        {
            Logger.Log($"Error stopping MCP process {serverId}: {ex.Message}", "ERROR");
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
            Logger.Log($"MCP [{serverId}] sent: {message}");

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
            Logger.Log($"Error calling MCP method in {serverId}: {ex.Message}", "ERROR");
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
            Logger.Log($"MCP [{serverId}] sent raw: {message}");
            return "OK";
        }
        catch (Exception ex)
        {
            Logger.Log($"Error sending to MCP process {serverId}: {ex.Message}", "ERROR");
            return $"ERROR: {ex.Message}";
        }
    }
    
    /// <summary>
    /// Прочитать следующее уведомление из очереди
    /// </summary>
    public McpNotification? NextNotification(string serverId)
    {
        if (_notificationQueues.TryGetValue(serverId, out var queue) && queue.TryDequeue(out var notification))
        {
            return notification;
        }
        return null;
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
