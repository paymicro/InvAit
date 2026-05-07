namespace ToolCore;

/// <summary>
/// Abstraction for logging to allow ToolCore to work without VS dependencies.
/// </summary>
public interface ILogger
{
    void Log(string message, string level = "INFO");
}
