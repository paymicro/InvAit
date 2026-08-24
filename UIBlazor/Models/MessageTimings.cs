namespace UIBlazor.Models;

/// <summary>
/// Тайминги сообщения
/// </summary>
public class MessageTimings
{
    public TimeSpan FirstToken { get; set; } = TimeSpan.Zero;

    public TimeSpan Reasoning { get; set; } = TimeSpan.Zero;

    public TimeSpan Content { get; set; } = TimeSpan.Zero;

    public TimeSpan Total { get; set; } = TimeSpan.Zero;

    public float TokensInSec { get; set; } = 0;

    /// <summary>Видимые токены ответа (без размышлений) — столько сообщение занимает в контексте.</summary>
    public int Tokens { get; set; } = 0;

    /// <summary>Токены размышлений, не входят в <see cref="Tokens"/> и место в контексте не занимают.</summary>
    public int ReasoningTokens { get; set; } = 0;
}
