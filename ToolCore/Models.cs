namespace ToolCore;

/// <summary>
/// ToolCore response wrapper
/// </summary>
public class ToolResponse
{
    public bool Success { get; set; } = true;
    public string? Payload { get; set; }
    public string? Error { get; set; }
}
