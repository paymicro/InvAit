namespace UIBlazor.Services.Interfaces;

public interface IToolCallHandler
{
    /// <summary>
    /// Processes all tool calls in the message and returns results.
    /// </summary>
    Task ProcessToolCallsAsync(
        VisualChatMessage message,
        List<ContentSegment> toolSegments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Handles approval response from user.
    /// </summary>
    Task HandleApprovalAsync(string segmentId, bool approved);
}
