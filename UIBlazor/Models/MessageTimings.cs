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
}
