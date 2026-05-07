namespace ToolCore.Standalone;

/// <summary>
/// Console logger with color support
/// </summary>
public class ConsoleLogger(bool verbose = false) : ILogger
{
    private readonly bool _verbose = verbose;

    public void Log(string message, string level = "INFO")
    {
        var color = level switch
        {
            "ERROR" => ConsoleColor.Red,
            "WARN" => ConsoleColor.Yellow,
            "DEBUG" => ConsoleColor.Gray,
            _ => ConsoleColor.White
        };

        if (level == "DEBUG" && !_verbose) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{timestamp}] [{level}] {message}");
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }
}
