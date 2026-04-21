namespace UIBlazor.Models;

/// <summary>
/// A lightweight summary of a conversation session for display in lists.
/// </summary>
public class SessionSummary
{
    public string Id { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// The first user message in the session, truncated for display.
    /// </summary>
    public string FirstUserMessage { get; set; } = string.Empty;
}
