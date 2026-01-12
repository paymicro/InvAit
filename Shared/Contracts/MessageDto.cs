namespace Shared.Contracts;

public class MessageDto
{
    /// <summary>
    /// user, assistant, system
    /// </summary>
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
