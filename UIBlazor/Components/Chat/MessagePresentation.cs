using System.Text.RegularExpressions;
using UIBlazor.Services.Settings;

namespace UIBlazor.Components.Chat;

/// <summary>
/// Lazy presentation preparation of a message: render segments, plan extraction, tool display
/// names, initial "show more" state.
/// <para>
/// All of it used to run over the WHOLE history when a session was loaded. With 500 messages the
/// regex plan extraction and the segment building were a measurable part of the freeze that made
/// WebView2 report <c>RenderProcessUnresponsive</c>. It is now done for the messages the window
/// actually materialises (see <see cref="MessageWindowPolicy"/>) and is idempotent, so a message
/// revealed later is prepared on its first render.
/// </para>
/// </summary>
public static class MessagePresentation
{
    private static readonly Regex PlanRegex = new(
        @"<plan>(?<plan>.*?)</plan>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(200));

    /// <summary>Marker that must be present before the plan regex is even attempted.</summary>
    private const string PlanOpenTag = "<plan";

    /// <summary>
    /// Prepares a message for rendering. Every step is guarded, so calling it on an
    /// already-prepared message costs three field reads.
    /// </summary>
    /// <param name="message">Message about to enter the DOM.</param>
    /// <param name="toolManager">Used to resolve a persisted tool result back to its display name.</param>
    public static void Prepare(VisualChatMessage message, IToolManager? toolManager = null)
    {
        if (message.IsTechnical)
            return;

        // A collapsed/expanded bubble: long messages open collapsed ("Show more").
        message.IsExpanded = IsShortMessage(message.DisplayContent ?? message.Content);

        if (message.Role == ChatMessageRole.Assistant)
            ApplyPlan(message);

        if (message.Segments.Count == 0 && !string.IsNullOrEmpty(message.Content))
            MessageParser.BuildHistorySegments(message.Content, message);

        if (message.ToolCalls is not { Count: > 0 } toolCalls || toolManager is null)
            return;

        // ToolResult.DisplayName is [JsonIgnore]: after a reload it is empty and the result header
        // would render blank. Resolved here instead of over the whole history at load time.
        foreach (var toolCall in toolCalls)
        {
            var result = toolCall.Result;
            if (result is null || !string.IsNullOrEmpty(result.DisplayName))
                continue;

            result.DisplayName = ToolResult.GetDisplayName(
                result.Success, toolManager.GetTool(result.Name)?.DisplayName ?? result.Name);
        }
    }

    /// <summary>
    /// Extracts a <c>&lt;plan&gt;</c> block into <see cref="VisualChatMessage.PlanContent"/> and
    /// removes it from <see cref="VisualChatMessage.DisplayContent"/>.
    /// <para>
    /// The regex runs with a 200 ms timeout, so the tag substring is checked first: an assistant
    /// message without a plan (the overwhelmingly common case) must not pay for a regex engine
    /// start-up on every render pass over the history.
    /// </para>
    /// </summary>
    public static void ApplyPlan(VisualChatMessage message)
    {
        if (message.Content is null || !message.Content.Contains(PlanOpenTag, StringComparison.Ordinal))
            return;

        var match = PlanRegex.Match(message.Content);
        if (!match.Success)
            return;

        message.PlanContent = match.Groups["plan"].Value.Trim();
        message.DisplayContent = PlanRegex.Replace(message.DisplayContent ?? message.Content, string.Empty).Trim();

        if (string.IsNullOrEmpty(message.DisplayContent))
            message.DisplayContent = "Proposed Plan:";
    }

    /// <summary>
    /// A short message opens expanded; a long one starts collapsed behind "Show more".
    /// </summary>
    public static bool IsShortMessage(string? content)
        => string.IsNullOrEmpty(content) || (content.Length < 1000 && content.Count(c => c == '\n') < 15);
}
