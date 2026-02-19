using System.Text.Json.Serialization;

namespace UIBlazor.Services.Models;

/// <summary>
/// A message received from the API, including the message text, index, and reason why the message finished.
/// </summary>
public class ChatChoice
{
    /// <summary>
    /// The index of the choice in the list of choices.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Non stream message.
    /// </summary>
    [JsonPropertyName("message")]
    public ChatDelta? Message { get; set; }

    /// <summary>
    /// The reason why the chat interaction ended after this choice was presented to the user
    /// </summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    /// <summary>
    /// Stream. Partial message.
    /// </summary>
    [JsonPropertyName("delta")]
    public ChatDelta? Delta { get; set; }
}
