namespace ToolCore;

/// <summary>
/// A text block taken apart into lines plus the one piece of metadata needed to put it back
/// together unchanged: whether it ended with a line break.
///
/// WHY THIS EXISTS: `FileUtils` and `EditApplier` each split text on their own, and the two answers
/// drifted — one dropped a trailing empty line, the other did not, and a search block that ended
/// with `\n` became a two-line search for a real empty line. There is now ONE splitter
/// (<see cref="FromText"/>) and ONE edit-text convention on top of it
/// (<see cref="SplitEditText"/>).
/// <para>
/// The separator and the mixed-endings flag are deliberately NOT computed here. They used to be,
/// from the decoded text, while `FileUtils` computed the same two answers a second time from the
/// BYTES — two scanners for one rule, and the text one was only ever read by tests. The bytes are
/// the authority (<see cref="TextFileCodec.DetectSeparator(byte[], int, Encoding)"/>).
/// </para>
/// </summary>
internal readonly struct TextLines
{
    private TextLines(List<string> lines, bool hasFinalNewLine)
    {
        Lines = lines;
        HasFinalNewLine = hasFinalNewLine;
    }

    /// <summary>The lines, WITHOUT the terminators; a final line break is not an extra empty line.</summary>
    public List<string> Lines { get; }

    /// <summary>True when the text ends with a line break — the last element of <see cref="Lines"/> does not carry it.</summary>
    public bool HasFinalNewLine { get; }

    /// <summary>
    /// THE splitter: CRLF, LF and a lone CR are all line breaks, so a mixed-ending file and a
    /// mixed-ending search block meet on the same list. ONE final line break is a TERMINATOR — it is
    /// reported in <see cref="HasFinalNewLine"/> and not as a trailing empty line, so re-joining
    /// with the separator reproduces the text. An empty text has no lines at all.
    /// </summary>
    public static TextLines FromText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new TextLines(new List<string>(), hasFinalNewLine: false);

        var hasFinalNewLine = text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal);
        var lines = new List<string>(text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None));

        if (hasFinalNewLine && lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return new TextLines(lines, hasFinalNewLine);
    }

    /// <summary>
    /// Splits an edit text (oldStr / newStr) into the line list an edit applies to. Mixed line
    /// endings are normalised here, and ONE trailing line break is a terminator, not an extra empty
    /// line: "line2\n" is the ONE-line block "line2". Without that, an oldStr copied from a numbered
    /// listing searched for "line2" followed by an EMPTY line and failed on any file whose next line
    /// has content. A SECOND trailing break is kept — the model really did address a trailing empty
    /// line. Null/empty yields a single empty line, which the legacy semantics read as "the empty
    /// line of the file".
    /// </summary>
    public static List<string> SplitEditText(string? text) =>
        string.IsNullOrEmpty(text) ? new List<string> { string.Empty } : FromText(text!).Lines;
}
