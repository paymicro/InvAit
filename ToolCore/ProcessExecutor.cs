using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ToolCore;

/// <summary>
/// Исполнитель внешних процессов (git, dotnet, sh), адаптированный для .NET Standard 2.0.
/// Обеспечивает жесткие таймауты, ограничение памяти и убийство всего дерева процессов.
/// </summary>
public class ProcessExecutor(ILogger logger)
{
    private readonly ILogger _logger = logger ?? new NullLogger();

    public ProcessExecutor() : this(new NullLogger())
    {
    }

    public const int DefaultOutputLimit = 30_000; // ~30 KB
    public const int MaxOutputLines = 50_000;

    private static readonly Dictionary<string, string> NonInteractiveEnv = new()
    {
        ["TERM"] = "dumb",                       // тупая консоль - никакого интерактива
        ["NO_COLOR"] = "1",                      // отключает ANSI-цвета
        ["GIT_PAGER"] = "cat",                   // git без пейджера
        ["GIT_CONFIG_PARAMETERS"] = "'color.ui=false'",
        ["GIT_TERMINAL_PROMPT"] = "0",           // запрещает Git запрашивать пароль
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",   // убирает телеметрию dotnet
        ["DOTNET_CLI_UI_LANGUAGE"] = "en-US",    // английский вывод dotnet
        ["DOTNET_NOLOGO"] = "true",              // убирает приветствие dotnet
        ["DOTNET_TERMINAL_LOGGER"] = "0",        // отключает terminal logger (перерисовку строк)
        ["LANG"] = "en_US.UTF-8",                // UTF-8 для утилит
        ["LC_ALL"] = "en_US.UTF-8",
    };

    public static void ConfigureNonInteractiveEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var kvp in NonInteractiveEnv)
        {
            startInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
        }
    }

    public static string? FindGitSh()
    {
        if (GetFullPathCommand("sh.exe") != null)
            return "sh.exe";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string[] registryPaths = [
                @"SOFTWARE\GitForWindows",
                @"SOFTWARE\WOW6432Node\GitForWindows"
            ];

            foreach (var path in registryPaths)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                    if (key?.GetValue("InstallPath") is string installPath && !string.IsNullOrEmpty(installPath))
                    {
                        var shPath = Path.Combine(installPath, "bin", "sh.exe");
                        if (File.Exists(shPath))
                            return shPath;
                    }
                }
                catch
                {
                    // Игнорируем ошибки доступа к реестру
                }
            }

            var defaultPath = @"C:\Program Files\Git\bin\sh.exe";
            if (File.Exists(defaultPath)) return defaultPath;
        }

        return null;
    }

    private static string? GetFullPathCommand(string commandName)
    {
        if (commandName.Contains(Path.DirectorySeparatorChar) || commandName.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(commandName) ? commandName : null;
        }

        var extensions = Environment.GetEnvironmentVariable("PATHEXT")?.Split(';')
            ?? [".exe", ".com", ".bat", ".cmd"];

        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();

        // Безопасность: Убираем текущую папку из приоритета поиска, сначала ищем в системе
        var searchPaths = paths.Concat([Directory.GetCurrentDirectory()]);

        var fileNameExt = Path.GetExtension(commandName).ToUpperInvariant();
        var hasExecutableExtension = !string.IsNullOrEmpty(fileNameExt) && extensions.Contains(fileNameExt);

        foreach (var directory in searchPaths)
        {
            if (string.IsNullOrEmpty(directory)) continue;
            var fullPathWithOriginalName = Path.Combine(directory, commandName);

            if (hasExecutableExtension && File.Exists(fullPathWithOriginalName))
            {
                return fullPathWithOriginalName;
            }

            foreach (var ext in extensions)
            {
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
    /// Прямой запуск исполняемого файла с передачей аргументов
    /// </summary>
    public async Task<ProcessResult> ExecuteAsync(
        string command,
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 30000,
        int outputLimit = DefaultOutputLimit,
        CancellationToken cancellationToken = default,
        CommandPolicy? policy = null)
    {
        try
        {
            // Политика применяется к полной командной строке (команда + аргументы)
            if (policy is not null && !policy.IsAllowed($"{command} {arguments}"))
            {
                _logger.Log($"Command blocked by policy: {command}", "WARNING");
                return new ProcessResult { Success = false, Error = $"Command '{command}' is blocked by the command policy." };
            }

            _logger.Log($"Executing command: {command} (arguments redacted)");

            string? fullCommand;
            if (command == "sh")
            {
                fullCommand = FindGitSh();
                if (fullCommand == null)
                {
                    return new ProcessResult { Success = false, Error = "Git sh not found. Bash execution is unavailable." };
                }
            }
            else
            {
                fullCommand = GetFullPathCommand(command);
                if (fullCommand == null)
                {
                    return new ProcessResult { Success = false, Error = $"Failed to find executable: {command} in system PATH." };
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fullCommand,
                Arguments = arguments,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };

            ConfigureNonInteractiveEnvironment(startInfo);

            return await RunProcessInternalAsync(startInfo, timeoutMs, outputLimit, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Log($"Process initialization error: {ex.Message}", "ERROR");
            return new ProcessResult { Success = false, Error = $"Initialization failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Запуск bash-скрипта/команды через временный файл.
    /// Надёжнее, чем '-c': нет проблем с экранированием кавычек и лимитом длины командной строки.
    /// </summary>
    public async Task<ProcessResult> ExecuteBashAsync(
        string bashScript,
        string? workingDirectory = null,
        int timeoutMs = 120_000,
        int outputLimit = DefaultOutputLimit,
        CancellationToken cancellationToken = default,
        CommandPolicy? policy = null)
    {
        var shPath = FindGitSh();
        if (shPath == null)
        {
            return new ProcessResult { Success = false, Error = "Git sh not found. Cannot execute bash commands." };
        }

        if (policy is not null && !policy.IsAllowed(bashScript))
        {
            _logger.Log("Bash command blocked by policy.", "WARNING");
            return new ProcessResult { Success = false, Error = "Bash command is blocked by the command policy." };
        }

        // Временный файл со скриптом — снимает проблемы экранирования и длины командной строки.
        var tempScript = Path.Combine(Path.GetTempPath(), $"invait_{Guid.NewGuid():N}.sh");
        try
        {
            // netstandard2.0 не имеет File.WriteAllTextAsync — используем синхронную запись.
            File.WriteAllText(tempScript, bashScript, new UTF8Encoding(false));

            // sh <файл> — скрипт передаётся через stdin файла, а не через аргументы.
            var arguments = $"\"{tempScript}\"";
            return await ExecuteAsync(shPath, arguments, workingDirectory, timeoutMs, outputLimit, cancellationToken, policy);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { /* Игнорируем ошибку удаления временного файла */ }
        }
    }
    private async Task<ProcessResult> RunProcessInternalAsync(
        ProcessStartInfo startInfo,
        int timeoutMs,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            return new ProcessResult { Success = false, Error = "Failed to start process." };
        }

        var processId = process.Id;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (s, e) => tcs.TrySetResult(true);

        if (process.HasExited)
        {
            tcs.TrySetResult(true);
        }

        // Читаем потоки асинхронно, поблочно и с лимитом
        var stdoutTask = ReadStreamLimitedAsync(process.StandardOutput, outputLimit, cancellationToken);
        var stderrTask = ReadStreamLimitedAsync(process.StandardError, outputLimit, cancellationToken);

        // Три независимых сигнала: выход процесса, таймаут, отмена.
        // Таймаут НЕ связываем с токеном отмены, чтобы не путать отмену с таймаутом.
        var exitTask = tcs.Task;
        var timeoutTask = Task.Delay(timeoutMs, CancellationToken.None);
        var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelReg = cancellationToken.Register(() => cancelTcs.TrySetResult(true));
        if (cancellationToken.IsCancellationRequested) cancelTcs.TrySetResult(true);
        var cancelTask = cancelTcs.Task;

        var completed = await Task.WhenAny(exitTask, timeoutTask, cancelTask);

        var timedOut = completed == timeoutTask;
        var cancelled = completed == cancelTask;
        var wasKilled = false;

        if (timedOut || cancelled)
        {
            wasKilled = true;
            KillProcessTree(processId); // Жестко убиваем всё дерево процессов

            await Task.WhenAny(exitTask, Task.Delay(1000, CancellationToken.None));
        }

        // Защищаем чтение стримов от вечного зависания зомби-процессов
        var streamsTimeout = Task.Delay(2000, CancellationToken.None);
        var readAllStreamsTask = Task.WhenAll(stdoutTask, stderrTask);

        if (await Task.WhenAny(readAllStreamsTask, streamsTimeout) == streamsTimeout)
        {
            _logger.Log($"Process streams reading timed out for PID {processId}.", "WARNING");
        }

        var stdout = stdoutTask.Status == TaskStatus.RanToCompletion
            ? stdoutTask.Result
            : "[Stream reading timed out]";
        var stderr = stderrTask.Status == TaskStatus.RanToCompletion
            ? stderrTask.Result
            : "[Stream reading timed out]";

        var exitCode = -1;
        try { exitCode = process.ExitCode; } catch { }

        var suffix = wasKilled
            ? (cancelled ? "\nProcess was cancelled by the caller." : $"\nCommand timed out after {timeoutMs}ms.")
            : string.Empty;

        var result = new ProcessResult
        {
            Success = exitCode == 0 && !wasKilled,
            Output = stdout,
            Error = string.Join("\n", stderr, suffix).Trim('\n'),
            ExitCode = exitCode,
            TimedOut = timedOut,
            Cancelled = cancelled,
            WasKilled = wasKilled
        };

        if (!result.Success && !wasKilled && !string.IsNullOrEmpty(stdout))
        {
            result.Error = string.Join("\n", stdout, stderr).Trim();
        }

        return result;
    }

    /// <summary>
    /// Потоковое поблочное чтение с лимитом для защиты памяти Visual Studio
    /// </summary>
    private static async Task<string> ReadStreamLimitedAsync(StreamReader reader, int outputLimit, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        var totalCharsRead = 0;
        var linesCount = 0;
        int read;

        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            if (cancellationToken.IsCancellationRequested) break;

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n') linesCount++;
            }

            if (totalCharsRead + read > outputLimit || linesCount > MaxOutputLines)
            {
                var allowed = Math.Min(read, outputLimit - totalCharsRead);
                if (allowed > 0)
                {
                    sb.Append(buffer, 0, allowed);
                }

                var truncatedMarker = $"\n[... Output truncated due to size/line limit ...]\n";
                sb.Insert(0, truncatedMarker);
                break;
            }

            sb.Append(buffer, 0, read);
            totalCharsRead += read;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Кроссплатформенное убийство всего дерева процессов для .NET Standard 2.0
    /// </summary>
    private static void KillProcessTree(int pid)
    {
        if (pid <= 0) return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/T /F /PID {pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                p?.WaitForExit(3000);
            }
            catch { }
        }
        else
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-9 -{pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                p?.WaitForExit(3000);
            }
            catch
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "kill",
                        Arguments = $"-9 {pid}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    p?.WaitForExit(3000);
                }
                catch { }
            }
        }
    }

    private class NullLogger : ILogger
    {
        public void Log(string message, string level = "INFO") { }
    }
}
