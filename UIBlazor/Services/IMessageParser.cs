namespace UIBlazor.Services;

/// <summary>
/// Service for parsing chat message content into segments.
/// </summary>
public interface IMessageParser
{
    /// <summary>
    /// Updates the segments of a visual chat message based on incoming text delta.
    /// </summary>
    /// <param name="delta">The incoming text chunk.</param>
    /// <param name="assistant">The assistant message being updated.</param>
    /// <param name="isHistory">Whether we are parsing history.</param>
    void UpdateSegments(string delta, VisualChatMessage assistant, bool isHistory = false);
}
