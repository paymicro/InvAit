using System.Text.Json.Serialization;

namespace UIBlazor.Services.Models;

public class StreamChunk
{
    /// <summary>
    /// Id ответа. Один на все чанки в одном стриме.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Имя модели, которая отвечает.
    /// Обычно только первый ответ содержит модель. Но некоторые ответы постоянно отдают имя весь стрим.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Всегда один элемент
    /// </summary>
    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; set; } = [];

    /// <summary>
    /// Usage information. Usually provided in the last chunk if stream_options: { include_usage: true } is set.
    /// </summary>
    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; set; }

    [JsonIgnore]
    public ChatChoice? Choice => Choices.FirstOrDefault();
}