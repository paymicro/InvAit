namespace Shared.Contracts;

/// <summary>
/// Сообщение от UI → Extension.
/// Ответ ожидается как <see cref="VsResponse"/>
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
    /// Для сопоставления ответа
    /// </summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
}
