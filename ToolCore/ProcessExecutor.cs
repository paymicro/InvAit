using System.Diagnostics;
using System.Text;

namespace ToolCore;

/// <summary>
/// Result of process execution
/// </summary>
public class ProcessResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
}

/// <summary>
/// Executor for running external processes (git, dotnet, cmd, etc.)
/// </summary>
public class ProcessExecutor
{
    private readonly ILogger _logger;

    public ProcessExecutor() : this(new NullLogger())
    {
    }

    public ProcessExecutor(ILogger logger)
    {
        _logger = logger ?? new NullLogger();
    }

    public static string? FindGitSh()
    {
        // 1. Проверяем PATH (вдруг он там всё-таки есть)
        if (GetFullPathCommand("sh.exe") != null)
            return "sh.exe";

        // 2. Ищем в реестре (64-бит и 32-бит)
        string[] registryPaths = [
            @"SOFTWARE\GitForWindows",
            @"SOFTWARE\WOW6432Node\GitForWindows"
        ];

        foreach (var path in registryPaths)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            var installPath = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrEmpty(installPath))
            {
                var shPath = Path.Combine(installPath, "bin", "sh.exe");
                if (File.Exists(shPath))
                    return shPath;
            }
        }

        // 3. Стандартный путь как последний шанс
        var defaultPath = @"C:\Program Files\Git\bin\sh.exe";
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    /// <summary>
    /// Find full path to command, searching in PATH if needed
    /// </summary>
    private static string? GetFullPathCommand(string commandName)
    {
        // If already has path separator, check as-is
        if (commandName.Contains(Path.DirectorySeparatorChar) || commandName.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(commandName) ? commandName : null;
        }

        // Get extensions from PATHEXT (.COM;.EXE;.BAT;.CMD etc.)
        var extensions = Environment.GetEnvironmentVariable("PATHEXT")?.Split(';')
            ?? [".exe", ".com", ".bat", ".cmd"];

        // Get paths from PATH
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        // Add current directory to search paths
        var searchPaths = new[] { Directory.GetCurrentDirectory() }.Concat(paths);

        var fileNameExt = Path.GetExtension(commandName).ToUpperInvariant();
        var hasExecutableExtension = !string.IsNullOrEmpty(fileNameExt) && extensions.Contains(fileNameExt);

        foreach (var directory in searchPaths)
        {
            var fullPathWithOriginalName = Path.Combine(directory, commandName);

            // If user already specified executable extension (e.g. npx.cmd)
            if (hasExecutableExtension && File.Exists(fullPathWithOriginalName))
            {
                return fullPathWithOriginalName;
            }

            // Try all possible extensions from PATHEXT
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
    /// Execute a command and wait for completion
    /// </summary>
    public async Task<ProcessResult> ExecuteAsync(
        string command,
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 30000)
    {
        _logger.Log($"Executing: {command} {arguments}");

        string fullCommand;

        fullCommand = command == "sh"
            ? FindGitSh() ?? "cmd"
            : GetFullPathCommand(command) ?? "cmd";
        if (fullCommand == null)
        {
            return new ProcessResult
            {
                Success = false,
                Error = $"Failed to find {command} in system."
            };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fullCommand,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        var env = new Dictionary<string, string>
        {
            ["TERM"] = "dumb",                      // тупая консоль - никакого интерактива
            ["GIT_PAGER"] = "cat",
            ["GIT_CONFIG_PARAMETERS"] = "'color.ui=false'",
            ["NO_COLOR"] = "1",
            ["GIT_TERMINAL_PROMPT"] = "0",          // Запрещает Git запрашивать пароль (просто упадет с ошибкой)
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",  // Немного ускорит dotnet и уберет лишний инфо-текст
            ["DOTNET_CLI_UI_LANGUAGE"] = "en-US",   // dotnet лучше на английском
            ["DOTNET_NOLOGO"] = "true",             // Убирает приветствие dotnet
            ["LANG"] = "en_US.UTF-8",               // UTF-8 для ультилит
            ["LC_ALL"] = "en_US.UTF-8",
        };

        // Set environment variables
        foreach (var kvp in env)
        {
            startInfo.Environment[kvp.Key] = kvp.Value;
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new ProcessResult
                {
                    Success = false,
                    Error = "Failed to start process"
                };
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var isTimedOut = !process.WaitForExit(timeoutMs);
            
            if (isTimedOut)
            {
                try { process.Kill(); } catch { /* Игнорируем, если уже умер */ }
            }

            await Task.WhenAll(outputTask, errorTask);

            var stdout = await outputTask;
            var stderr = await errorTask;

            var result = new ProcessResult
            {
                Success = process.ExitCode == 0 && !isTimedOut,
                Output = stdout,
                ExitCode = process.ExitCode
            };

            if (!result.Success)
            {
                result.Error = string.Join("\n", stdout, stderr);

                if (isTimedOut)
                {
                    result.Error += $"\nCommand timed out after {timeoutMs}ms";
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.Log($"Process execution error: {ex.Message}", "ERROR");
            return new ProcessResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Execute a shell command (sh -c ...)
    /// </summary>
    public async Task<ProcessResult> ExecuteBashAsync(
        string command,
        string? workingDirectory = null,
        int timeoutMs = 30000)
    {
        return await ExecuteAsync("sh", $"-c '{command.Replace('\\', '/').Replace("'", "'\\''")}'", workingDirectory, timeoutMs);
    }

    /// <summary>
    /// Execute dotnet command
    /// </summary>
    public async Task<ProcessResult> ExecuteDotnetAsync(
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 60000)
    {
        return await ExecuteAsync("dotnet", arguments, workingDirectory, timeoutMs);
    }

    /// <summary>
    /// Execute git command
    /// </summary>
    public async Task<ProcessResult> ExecuteGitAsync(
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 30000)
    {
        return await ExecuteAsync("git", arguments, workingDirectory, timeoutMs);
    }

    private class NullLogger : ILogger
    {
        public void Log(string message, string level = "INFO") { }
    }
}
