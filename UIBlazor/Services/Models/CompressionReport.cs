namespace UIBlazor.Services.Models;

/// <summary>
/// Result of one successful context compression. Compression rewrites the session history
/// somewhere in the MIDDLE of the chat, so the UI needs to know which message to reveal and
/// how many messages disappeared — otherwise the user just sees part of the history vanish.
/// </summary>
/// <param name="Session">Session whose history was rewritten (main chat or a sub-agent branch).</param>
/// <param name="Summary">Message inserted in place of the collapsed turns.</param>
/// <param name="CollapsedCount">Number of messages replaced by the summary.</param>
public sealed record CompressionReport(ConversationSession Session, VisualChatMessage Summary, int CollapsedCount);
