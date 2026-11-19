namespace UIBlazor.Components.Chat;

/// <summary>
/// Kind of a row in the rendered transcript.
/// </summary>
public enum WindowRowKind
{
    /// <summary>A real message, rendered as <c>ChatMessageView</c>.</summary>
    Message,

    /// <summary>A collapsed archive of consecutive messages, rendered as the plaque.</summary>
    Group
}

/// <summary>
/// One row of the render plan. <see cref="Index"/> is a REAL index into the session snapshot
/// (not into the visible window), so <c>IsLast</c> and every other positional decision stay
/// correct while the middle of the history is hidden.
/// </summary>
public readonly record struct WindowRow(WindowRowKind Kind, int Index, int Count);

/// <summary>Aggregate of the hidden range, shown on the plaque.</summary>
public sealed record MessageWindowStats(int Messages, int ToolCalls, int ApproxTokens);

/// <summary>
/// What the transcript must materialise: head rows, one optional group, tail rows.
/// </summary>
public sealed record MessageWindowPlan(
    IReadOnlyList<WindowRow> Rows,
    int HeadCount,
    int TailStart,
    int HiddenCount,
    MessageWindowStats HiddenStats)
{
    /// <summary>True when a plaque row is part of the plan.</summary>
    public bool HasGroup => HiddenCount > 0;
}

/// <summary>
/// Pure policy of the history window: which messages of a session are materialised in the DOM.
/// <para>
/// A 500-message transcript used to be rendered in full: one <c>BuildRenderTree</c> over 500
/// bubbles, one JS interop per markdown block (<c>marked.parse</c> of the whole text +
/// <c>innerHTML</c> replacement), all on the single WASM thread and without ever yielding. That
/// is what the WebView2 watchdog reported as <c>RenderProcessUnresponsive</c>.
/// </para>
/// <para>
/// The layout mirrors <see cref="Services.ChatService.PlanCompression"/> on purpose - the same
/// <c>[head][middle][tail]</c> model, so "what the user reads" and "what the model keeps verbatim"
/// cannot drift apart conceptually. The MIDDLE is not deleted: it is hidden behind a plaque that
/// states its size, its tool count and its context cost, because a silently missing block of
/// history reads as data loss (the lesson already paid for on compression).
/// </para>
/// </summary>
public static class MessageWindowPolicy
{
    /// <summary>
    /// First messages stay visible ALWAYS: that is where a task starts, and it is what a user
    /// scrolls up to re-read. Hiding the task to save ten bubbles is a bad trade.
    /// </summary>
    public const int HeadWindowCount = 10;

    /// <summary>Newest messages always materialised. Sized to cover one screen of work.</summary>
    public const int TailWindowCount = 30;

    /// <summary>How many hidden messages one "show earlier" click reveals.</summary>
    public const int GroupPageSize = 30;

    /// <summary>
    /// Below this the plaque costs more attention than it saves: a 28-message chat renders whole.
    /// </summary>
    public const int MinHiddenCount = 20;

    /// <summary>
    /// Tail start the plan would use without a pin. Grows monotonically with the history, so an
    /// unpinned boundary MOVES FORWARD (hides more) as messages arrive.
    /// </summary>
    public static int DesiredTailStart(int messageCount, int expandedPages)
    {
        var window = TailWindowCount + Math.Max(0, expandedPages) * GroupPageSize;
        return Math.Max(Math.Min(HeadWindowCount, messageCount), messageCount - window);
    }

    /// <summary>
    /// Builds the render plan.
    /// </summary>
    /// <param name="messages">Session snapshot, oldest first.</param>
    /// <param name="expandedPages">How many extra pages the user already revealed.</param>
    /// <param name="pinnedTailStart">
    /// Boundary frozen at the start of the running turn. While a run streams, a boundary that moves
    /// forward would swallow the bubble the user is watching, and the scroll position would jump -
    /// so the caller pins it and this method takes the MINIMUM of the pin and the desired start.
    /// A pin can therefore only ever reveal, never hide; the caller lifts it after the run.
    /// </param>
    public static MessageWindowPlan Build(
        IReadOnlyList<VisualChatMessage> messages,
        int expandedPages = 0,
        int? pinnedTailStart = null)
    {
        var total = messages.Count;
        var headCount = Math.Min(HeadWindowCount, total);
        var desiredTailStart = DesiredTailStart(total, expandedPages);

        var tailStart = desiredTailStart;
        if (pinnedTailStart is { } pinned)
            tailStart = Math.Min(tailStart, Math.Max(headCount, pinned));

        tailStart = Math.Clamp(tailStart, headCount, total);

        // A message that needs the user's attention must never sit behind the plaque: an approval
        // gate inside a collapsed range is an invisible hang.
        for (var i = headCount; i < tailStart; i++)
        {
            if (!IsProtected(messages[i]))
                continue;

            tailStart = i;
            break;
        }

        var hiddenCount = tailStart - headCount;
        if (hiddenCount < MinHiddenCount)
        {
            // Too little to be worth a plaque: show everything from the head onwards.
            tailStart = headCount;
            hiddenCount = 0;
        }

        var rows = new List<WindowRow>(Math.Min(total, headCount + (total - tailStart) + 1));

        for (var i = 0; i < headCount; i++)
            rows.Add(new WindowRow(WindowRowKind.Message, i, 1));

        if (hiddenCount > 0)
            rows.Add(new WindowRow(WindowRowKind.Group, headCount, hiddenCount));

        for (var i = tailStart; i < total; i++)
            rows.Add(new WindowRow(WindowRowKind.Message, i, 1));

        var stats = hiddenCount > 0
            ? Measure(messages, headCount, hiddenCount)
            : new MessageWindowStats(0, 0, 0);

        return new MessageWindowPlan(rows, headCount, tailStart, hiddenCount, stats);
    }

    /// <summary>
    /// Messages the render plan materialises, as real indices. Used to prepare (segments, plan
    /// extraction, tool display names) ONLY what is about to be rendered - the whole point of the
    /// window is that the rest of the history costs nothing until it is revealed.
    /// </summary>
    public static IEnumerable<int> MaterialisedIndices(MessageWindowPlan plan)
    {
        foreach (var row in plan.Rows)
        {
            if (row.Kind != WindowRowKind.Message)
                continue;

            yield return row.Index;
        }
    }

    /// <summary>
    /// A message whose state demands an action from the user (or is still being written) must stay
    /// in the DOM even if the window maths put it in the middle.
    /// </summary>
    public static bool IsProtected(VisualChatMessage message)
    {
        if (message.IsStreaming)
            return true;

        if (message.ToolCalls is not { Count: > 0 } toolCalls)
            return false;

        foreach (var toolCall in toolCalls)
        {
            if (toolCall.ApprovalStatus == ToolApprovalStatus.Pending)
                return true;

            if (toolCall.IsExecuting)
                return true;

            // ask_user blocks the run until the user answers; the answer widget lives in the block.
            if (toolCall.Function.Name == BasicEnum.AskUser && toolCall.Result is null)
                return true;

            if (toolCall.SubAgents is not { Count: > 0 } branches)
                continue;

            foreach (var branch in branches)
            {
                if (branch.Status is SubAgentStatus.Pending or SubAgentStatus.Running
                    or SubAgentStatus.NeedAction or SubAgentStatus.Paused)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Statistics of the hidden range. The token figure uses the same ~4 chars per token heuristic
    /// as the rest of the UI (it is an order of magnitude, not accounting), and skips technical
    /// notes for the same reason the wire history does: the model never sees them.
    /// </summary>
    private static MessageWindowStats Measure(
        IReadOnlyList<VisualChatMessage> messages, int from, int count)
    {
        var toolCalls = 0;
        var chars = 0;

        for (var i = from; i < from + count && i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.IsTechnical)
                continue;

            chars += message.Content?.Length ?? 0;

            if (message.ToolCalls is not { Count: > 0 } calls)
                continue;

            toolCalls += calls.Count;
            foreach (var toolCall in calls)
            {
                chars += toolCall.Function.Arguments?.Length ?? 0;
                chars += toolCall.Result?.Content?.Length ?? 0;
            }
        }

        return new MessageWindowStats(count, toolCalls, chars / 4);
    }
}
