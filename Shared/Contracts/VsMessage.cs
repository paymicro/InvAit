namespace Shared.Contracts;

/// <summary>
/// Сообщение Extension → UI.
/// Инициатор Extension. Ответа может не быть.
/// Если ответ будет, то цепочка выглядит так <see cref="VsMessage"/> → <see cref="VsResponse"/> → <see cref="VsRequest"/>.
/// </summary>
public class VsMessage
{
    /// <summary>
    /// Тип события
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Для сопоставления ответа, если он будет
    /// </summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Нагрузка в JSON
    /// </summary>
    public string? Payload { get; set; }
}
