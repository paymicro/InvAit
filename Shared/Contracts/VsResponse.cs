namespace Shared.Contracts;

/// <summary>
/// Ответ на <see cref="VsRequest"/> от Extension → UI.
/// <para>
/// Контракт бинарный — дискриминатор <see cref="Success"/>, отдельного канала ошибки нет
/// (форма совпадает с MCP <c>CallToolResult { content, isError }</c>):
/// </para>
/// <list type="bullet">
/// <item><b>Success = true</b> — <see cref="Payload"/> содержит контент результата.</item>
/// <item><b>Success = false</b> — <see cref="Payload"/> содержит причину отказа
/// (транспортный сбой или доменный отказ инструмента). Пустым он быть не должен.</item>
/// <item><b>Partial success</b> — это <c>Success = true</c> с деталями в структуре
/// результата (например <see cref="FileContent.Error"/> по каждому файлу).</item>
/// </list>
/// </summary>
public class VsResponse
{
    /// <summary>
    /// Должен быть такой же, как в отправленном <see cref="VsRequest"/>.
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Удачно. Определяет, как читать <see cref="Payload"/>: контент или причина отказа.
    /// </summary>
    public bool Success { get; set; } = true;

    /// <summary>
    /// При <see cref="Success"/> = true — контент результата; при false — причина отказа.
    /// </summary>
    public string? Payload { get; set; }
}
