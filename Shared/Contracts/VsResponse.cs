using System.Text.Json.Serialization;

namespace Shared.Contracts;

/// <summary>
/// Ответ от Extension → UI
/// </summary>
public class VsResponse
{
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// JSON ответ
    /// </summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }
}
