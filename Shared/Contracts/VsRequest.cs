namespace Shared.Contracts;

/// <summary>
/// Сообщение от UI → Extension
/// </summary>
public class VsRequest
{
    /// <summary>
    /// "getActiveDocument", "insertText" и т.д.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// JSON-строка или serialized object
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>
    /// для сопоставления ответа
    /// </summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
}
