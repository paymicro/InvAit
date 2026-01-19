namespace UIBlazor.Agents;

/// <summary>
/// LLM wanted to call tool.
/// </summary>
public class VsToolToCall
{
    public Tool Tool { get; set; } = null!;

    public string ArgumentsJson { get; set; } = string.Empty;

    public bool IsApproved { get; set; }

    public bool IsProcessed { get; set; }

    public string CallId { get; set; } = Guid.NewGuid().ToString();

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    public VsToolResult? Result { get; set; }
}
