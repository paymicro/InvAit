using System.Text.Json.Serialization;

namespace Shared.Contracts;

public class MessageDto
{
    /// <summary>
    /// user, assistant, system
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
