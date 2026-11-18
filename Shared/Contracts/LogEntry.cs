namespace Shared.Contracts;

/// <summary>
/// Запись лога из UI (Blazor WASM) в хост (VS Output / VS Code OutputChannel).
/// Переносится fire-and-forget через <see cref="BasicEnum.LogError"/> (без VsResponse).
/// </summary>
public class LogEntry
{
    public const string ErrorLevel = "ERROR";
    public const string WarnLevel = "WARN";
    public const string InfoLevel = "INFO";

    /// <summary>ERROR / WARN / INFO.</summary>
    public string Level { get; set; } = ErrorLevel;

    /// <summary>Контекст возникновения (например "HTTP transport", "Notification", "VsBridge").</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Человекочитаемое сообщение.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Опционально: тип + сообщение + stack исключения.</summary>
    public string? Exception { get; set; }

    /// <summary>UTC-время возникновения (ISO 8601).</summary>
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
}
