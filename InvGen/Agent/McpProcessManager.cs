using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using InvGen.Utils;

namespace InvGen.Agent;

/// <summary>
/// Управление MCP процессами (NPX серверы)
/// </summary>
public class McpProcessManager
{
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _outputQueues = new();
    
    /// <summary>
    /// Запустить MCP процесс
    /// </summary>
    public async Task<string> StartProcessAsync(string serverId, string command, string args)
    {
        if (_processes.ContainsKey(serverId))
        {
            return $"ERROR: Process {serverId} already running";
        }
        
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command, // "npx" или "node"
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            
            var process = Process.Start(startInfo);
            if (process == null)
            {
                return "ERROR: Failed to start process";
            }
            
            _processes[serverId] = process;
            _outputQueues[serverId] = new ConcurrentQueue<string>();
            
            // Асинхронное чтение stdout
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.StandardOutput.EndOfStream)
                    {
                        var line = await process.StandardOutput.ReadLineAsync();
                        if (line != null)
                        {
                            _outputQueues[serverId].Enqueue(line);
                            Logger.Log($"MCP [{serverId}] stdout: {line}");
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
                            Logger.Log($"MCP [{serverId}] stderr: {line}", "WARN");
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
            _outputQueues.TryRemove(serverId, out _);
            
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
    /// Отправить сообщение в stdin процесса
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
            Logger.Log($"MCP [{serverId}] sent: {message}");
            return "OK";
        }
        catch (Exception ex)
        {
            Logger.Log($"Error sending to MCP process {serverId}: {ex.Message}", "ERROR");
            return $"ERROR: {ex.Message}";
        }
    }
    
    /// <summary>
    /// Прочитать сообщение из stdout процесса
    /// </summary>
    public async Task<string> ReadMessageAsync(string serverId, int timeoutMs = 5000)
    {
        if (!_outputQueues.TryGetValue(serverId, out var queue))
        {
            return "ERROR: Process not found";
        }
        
        var startTime = DateTime.UtcNow;
        
        while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
        {
            if (queue.TryDequeue(out var message))
            {
                Logger.Log($"MCP [{serverId}] received: {message}");
                return message;
            }
            
            await Task.Delay(50);
        }
        
        return "ERROR: Timeout waiting for message";
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
