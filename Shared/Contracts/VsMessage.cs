using System.Text.Json.Serialization;

namespace Shared.Contracts;

public class VsMessage
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
