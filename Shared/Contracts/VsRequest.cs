using System.Text.Json.Serialization;

namespace Shared.Contracts;

/// <summary>
/// Сообщение от UI → Extension
/// </summary>
public class VsRequest
{
    /// <summary>
    /// "getActiveDocument", "insertText" и т.д.
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// JSON-строка или serialized object
    /// </summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    /// <summary>
    /// для сопоставления ответа
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
}
