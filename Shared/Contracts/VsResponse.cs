namespace Shared.Contracts;

/// <summary>
/// Ответ от Extension → UI
/// </summary>
public class VsResponse
{
    public string CorrelationId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// JSON ответ
    /// </summary>
    public string? Payload { get; set; }
}
