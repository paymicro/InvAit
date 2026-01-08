namespace Shared.Contracts;

public class VsMessage
{
    public string Action { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public object? Payload { get; set; }
    public string? Error { get; set; }
}
