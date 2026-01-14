namespace Shared.Contracts;

/// <summary>
/// Ответ на <see cref="VsRequest"/> от Extension → UI
/// </summary>
public class VsResponse
{
    /// <summary>
    /// Должен быть такой же как в отправленном <see cref="VsResponse"/>
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Удачно
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Ошибка если <see cref="Success"/> is false
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Ответ
    /// </summary>
    public string? Payload { get; set; }
}
