namespace UIBlazor.Models;

/// <summary>
/// Тайминги сообщения
/// </summary>
public class MessageTimings
{
    /// <summary>Время до первого токена (задержка провайдера + TTFT), не время генерации.</summary>
    public TimeSpan FirstToken { get; set; } = TimeSpan.Zero;

    /// <summary>Сколько заняла генерация размышлений (от первого токена до первого видимого токена).</summary>
    public TimeSpan Reasoning { get; set; } = TimeSpan.Zero;

    /// <summary>Сколько заняла генерация видимого ответа.</summary>
    public TimeSpan Content { get; set; } = TimeSpan.Zero;

    /// <summary>Полное время тура, включая ожидание первого токена.</summary>
    public TimeSpan Total { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Скорость генерации: <see cref="Tokens"/> делить на окно от первого токена до последнего
    /// (время до первого токена из делителя исключено — в это время ничего не генерировалось).
    /// Считается в <see cref="Services.MessageTimingsCalculator"/>.
    /// </summary>
    public float TokensInSec { get; set; } = 0;

    /// <summary>
    /// Полный вывод модели: ответ И размышления. Это счётчик генерации; место в контексте —
    /// <see cref="ConversationSession.TotalTokens"/>, где размышления не учитываются.
    /// </summary>
    public int Tokens { get; set; } = 0;

    /// <summary>Из <see cref="Tokens"/>: доля размышлений (входит в скорость, не входит в контекст).</summary>
    public int ReasoningTokens { get; set; } = 0;
}
