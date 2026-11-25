namespace ToolCore;

/// <summary>
/// Line-based fuzzy search and replace for applying diff edits.
///
/// INPUT (SplitEditText — the ONE place where an edit text becomes a line list):
/// - CRLF, LF and a lone CR are all line breaks, so a mixed-ending file and a
///   mixed-ending search block meet on the same list;
/// - ONE trailing line break is a terminator, not an extra empty line: "line2\n" is
///   the one-line block "line2". Without this an oldStr copied from a numbered file
///   listing searched for "line2" followed by an EMPTY line and failed on any file
///   where the next line is not empty;
/// - a SECOND trailing break is kept — the model really did address a trailing empty line.
/// The applier itself never sees a line ending: it works on an already-split list.
///
/// MATCHING (case-insensitive; search lines are trimmed, so their own indentation is
/// ignored; search lines must occupy consecutive target lines):
/// - the FIRST line of a multi-line block may be truncated at the START (the target line
///   ends with the search line — the model lost the leading characters);
/// - the LAST line may be truncated at the END (the target line starts with the search
///   line — the model cut the tail mid-token);
/// - MIDDLE lines must match the target line exactly;
/// - a SINGLE-line search matches as a substring of the target line;
/// - empty/whitespace-only search lines match empty/whitespace-only target lines;
/// - an all-empty search block matches the first empty target line.
/// Full search (hint = -1) prefers a block where every line matches exactly over a
/// fragment match on an earlier line. A hint restricts the preferred range to
/// [hint - 1 - tol, hint - 1 + tol] (1-based, inclusive); when the window holds nothing the
/// search falls back to the whole candidate set, and if that still leaves several candidates
/// the edit is refused — there is no "first match anywhere" fallback.
///
/// REPLACEMENT (ApplyEdit, in place):
/// - a fully matched line is replaced VERBATIM by its replacement line (the replacement
///   keeps its own indentation, e.g. a re-nested block);
/// - a truncated boundary line: only the matched fragment is removed/replaced — the rest
///   of the line (leading indentation and the trailing part) is preserved. For the first
///   boundary line the matched fragment is the RIGHTMOST occurrence of the search text
///   (the suffix of the line); the trailing part of the last matched line stays attached
///   to the last emitted line (after extra replacement lines, when present), or — when
///   the whole matched block is deleted — to the preceding text (or becomes the first
///   line if the block starts the file);
/// - ANY single-line search that matched as a substring removes ONLY that substring, whether it
///   sits at column 0 or after an indentation: `x();` inside `x(); // needed by X` leaves the
///   comment behind. A column-0 fragment is NOT a whole line — replacing the whole line there
///   silently ate the rest of it (`x(); // keep`, `return; // early`, `break; // fallthrough` are
///   everywhere) and made the outcome depend on the line's indentation;
/// - a missing replacement line (replace shorter than search) removes what that search line
///   matched: a fully matched line disappears, a fragment-matched line only loses the fragment and
///   keeps the rest (see the fragment rule above). Inside a multi-line block the whole-line rule
///   applies; a LONE whole line cannot be deleted through the edit contract, because
///   <see cref="SplitEditText"/> turns `""` into [""] — `newStr: ""` leaves an EMPTY line, and no
///   JSON shape produces an empty replace list. Use `create_file` (overwrite), or a block whose
///   replacement has fewer lines than the search;
/// - extra replacement lines are inserted after the last matched line;
/// - an all-empty search block replaces the matched empty lines (one per search line) with the
///   replacement block.
///
/// AMBIGUITY (see <see cref="EditOutcome"/>): an edit that matches several places is REFUSED, not
/// applied to the first one, unless the hint singles one out or `replaceAll` is set. Silent
/// first-of-many is how an edit lands in the wrong block with a success message.
/// </summary>
public static class EditApplier
{
    /// <summary>
    /// Splits an edit text (oldStr / newStr) into the line list consumed by
    /// <see cref="ApplyEdit"/>. Mixed line endings are normalised in <see cref="TextLines"/>
    /// and nowhere else, and one trailing line break is treated as a terminator (see the type
    /// remarks).
    /// </summary>
    /// <param name="text">Raw edit text as sent by the model</param>
    /// <returns>At least one element; an empty/absent text yields a single empty line.</returns>
    public static List<string> SplitEditText(string? text) => TextLines.SplitEditText(text);

    /// <summary>
    /// Applies ONE edit to the target lines in place: finds the search block (fuzzy, case-insensitive)
    /// and replaces it with the replace block.
    ///
    /// AMBIGUITY IS A REFUSAL (this is the whole point of the result type): when the block occurs in
    /// several places and nothing disambiguates it, nothing is applied and the caller gets
    /// <see cref="EditOutcome.Ambiguous"/> with every candidate line. Replacing the first occurrence
    /// silently is how an edit lands in the wrong method — the model cannot even see it happened.
    /// A hint is what disambiguates; `replaceAll` is how the model says "I mean all of them".
    /// </summary>
    /// <param name="target">File lines (modified in place)</param>
    /// <param name="search">Lines to search for</param>
    /// <param name="replace">Replacement lines</param>
    /// <param name="hint">1-based position hint (or -1 for full search)</param>
    /// <param name="tol">Offset tolerance from hint</param>
    /// <param name="replaceAll">Replace every occurrence instead of requiring a single one</param>
    public static EditApplyResult ApplyEdit(List<string>? target, List<string>? search, List<string>? replace, int hint, int tol, bool replaceAll = false)
    {
        if (target == null || search == null || replace == null)
            return EditApplyResult.NotFound();
        if (search.Count == 0 || search.Count > target.Count)
            return EditApplyResult.NotFound();

        var searchTrimmed = search.Select(l => l.Trim()).ToArray();
        var matches = FindAllMatches(target, searchTrimmed);
        if (matches.Count == 0)
            return EditApplyResult.NotFound();

        // An all-empty search block is settled FIRST, BEFORE `replaceAll` is looked at. It carries no
        // text that could disambiguate it, so the legacy rule stands: the hinted line if any,
        // otherwise the FIRST empty line. Letting `replaceAll` have it would rewrite EVERY blank line
        // in the file — `oldStr: "\n"` (a very natural spelling of "a blank line") splits to one
        // empty search line, which changes the scope from one line to the whole file.
        // The VS Code twin carries the SAME guard in `applyEdit`; the two must not be allowed to
        // disagree about the scope of an empty pattern (see docs/dev/edit-matching.md).
        if (IsAllEmpty(searchTrimmed))
        {
            var empty = FirstEmptyLine(matches, hint, tol);
            ApplyAt(target, empty, searchTrimmed, replace);
            return EditApplyResult.Applied(empty, 1, matches);
        }

        if (replaceAll)
        {
            // Bottom-to-top so an already-applied replacement cannot shift a pending index. Overlapping
            // candidates (a single-line search inside a repeated block) are skipped: they are not
            // distinct occurrences any more once the later one was rewritten.
            var applied = 0;
            var firstLine = -1;
            var boundary = int.MaxValue;
            for (var k = matches.Count - 1; k >= 0; k--)
            {
                var match = matches[k];
                if (match + searchTrimmed.Length > boundary)
                    continue;

                ApplyAt(target, match, searchTrimmed, replace);
                boundary = match;
                firstLine = match;
                applied++;
            }

            return EditApplyResult.Applied(firstLine, applied, matches);
        }

        // The all-empty block never reaches here — `ApplyEdit` settles it before this point, so
        // `replaceAll` cannot change its scope and its refusal is not a guess the model could fix.
        var selected = SelectSingle(target, matches, searchTrimmed, hint, tol);
        if (selected == null)
            return EditApplyResult.Ambiguous(matches);

        ApplyAt(target, selected.Value, searchTrimmed, replace);
        return EditApplyResult.Applied(selected.Value, 1, matches);
    }

    /// <summary>
    /// Picks the ONE occurrence to edit, or null when the choice is a guess.
    /// A null result is the whole contract of "unambiguous": the caller must refuse rather than
    /// replace the first candidate (which is how an edit silently lands in the wrong method).
    ///
    /// Order of the tie-breakers: the hint window narrows the set FIRST (and is ignored when it holds
    /// nothing), then an exact whole-line block beats a fragment hit, then a single remaining
    /// candidate wins.
    /// </summary>
    private static int? SelectSingle(List<string> target, List<int> matches, string[] searchTrimmed, int hint, int tol)
    {
        if (matches.Count == 0)
            return null;

        // The all-empty block never reaches here — `ApplyEdit` settles it before this point, so
        // `replaceAll` cannot change its scope and its refusal is not a guess the model could fix.
        if (matches.Count == 1)
            return matches[0];

        var candidates = matches;
        if (hint >= 0)
        {
            var inRange = InWindow(matches, hint, tol);
            if (inRange.Count > 0)
                candidates = inRange;
        }

        if (candidates.Count == 1)
            return candidates[0];

        var wholeLine = candidates.Where(m => IsWholeLineBlock(target, m, searchTrimmed)).ToList();
        return wholeLine.Count == 1 ? wholeLine[0] : null;
    }

    /// <summary>Match indices whose 0-based start line falls into the hint's tolerance window.</summary>
    private static List<int> InWindow(List<int> matches, int hint, int tol)
    {
        var effectiveTolerance = Math.Max(0, tol);
        var minLine = Math.Max(0, hint - 1 - effectiveTolerance);
        var maxLine = hint - 1 + effectiveTolerance;
        return matches.Where(m => m >= minLine && m <= maxLine).ToList();
    }

    /// <summary>The single match inside the hint window, or null when the window holds none or several.</summary>
    private static int? PickInWindow(List<int> matches, int hint, int tol)
    {
        if (hint < 0)
            return null;
        var inRange = InWindow(matches, hint, tol);
        return inRange.Count == 1 ? inRange[0] : null;
    }

    /// <summary>
    /// The one empty line an all-empty search settles on: the FIRST candidate inside the hint window,
    /// otherwise the first one in the file.
    /// <para>
    /// NOT <see cref="PickInWindow"/>: that answers "is the choice unambiguous", and an empty search is
    /// never ambiguous — several blank lines in the window must not push the edit to the top of the
    /// file, which is what `PickInWindow(…) ?? matches[0]` did. The hint is the whole point of an
    /// empty search: it is the only thing that can place it.
    /// </para>
    /// </summary>
    private static int FirstEmptyLine(List<int> matches, int hint, int tol)
    {
        if (hint >= 0)
        {
            var inRange = InWindow(matches, hint, tol);
            if (inRange.Count > 0)
            {
                // NEAREST to the hint, not the first in the window. The window is ±5 lines, so the
                // earliest candidate can sit five lines ABOVE the line the model pointed at: an
                // "insert a section header here" call with hint 33 and blank lines at 30 and 32
                // landed on 30, matched nothing the model asked for, and still answered
                // "1 replacements". A tie keeps the earlier line, so the answer stays deterministic.
                var target = inRange[0];
                foreach (var candidate in inRange)
                {
                    if (Math.Abs(candidate - (hint - 1)) < Math.Abs(target - (hint - 1)))
                        target = candidate;
                }
                return target;
            }
        }

        return matches[0];
    }

    /// <summary>
    /// True when the trimmed search block carries no text at all. An all-empty block is matched by
    /// <see cref="LinesMatch"/> like any other, but both the selection (<see cref="SelectSingle"/>)
    /// and the replacement (<see cref="ApplyAt"/>) give it its own rule — so the test has ONE
    /// definition.
    /// </summary>
    private static bool IsAllEmpty(string[] searchTrimmed) => searchTrimmed.All(s => s.Length == 0);

    /// <summary>
    /// Every start line where the (trimmed) search block matches, in ascending order.
    /// The single matcher for the whole type: <see cref="ApplyEdit"/> needs the COUNT to decide
    /// between "apply" and "refuse as ambiguous", so "find the first one" is not enough here.
    /// </summary>
    private static List<int> FindAllMatches(List<string> target, string[] searchTrimmed)
    {
        var matches = new List<int>();
        for (var i = 0; i + searchTrimmed.Length <= target.Count; i++)
        {
            if (LinesMatch(target, i, searchTrimmed))
                matches.Add(i);
        }

        return matches;
    }

    /// <summary>Replaces the already-located block at <paramref name="start"/> (in place).</summary>
    private static void ApplyAt(List<string> target, int start, string[] searchTrimmed, List<string> replace)
    {
        // All search lines are empty: replace the matched empty LINES with the replacement block.
        // Every line of the block matched, so every one of them is consumed — removing only the
        // first one left the replacement a line early and a blank line behind.
        if (IsAllEmpty(searchTrimmed))
        {
            target.RemoveRange(start, searchTrimmed.Length);
            for (var j = replace.Count - 1; j >= 0; j--)
                target.Insert(start, replace[j]);
            return;
        }

        var result = new List<string>(target.Count);
        for (var i = 0; i < start; i++)
            result.Add(target[i]);

        string? lastSuffix = null;
        for (var j = 0; j < searchTrimmed.Length; j++)
        {
            var raw = target[start + j];
            var s = searchTrimmed[j];
            var hasReplacement = j < replace.Count;

            // Whole-line match: the replacement is used VERBATIM (a complete line with its
            // own indentation, e.g. a re-nested block). A missing replacement DELETES the line.
            if (string.Equals(raw.Trim(), s, StringComparison.OrdinalIgnoreCase))
            {
                if (hasReplacement)
                    result.Add(replace[j]);
                continue;
            }

            // Fragment match — a truncated boundary line of a block, or ANY single-line substring
            // match: only the matched part is removed/replaced; the rest of the line (leading
            // indentation and the trailing part) is preserved. There is deliberately no
            // "fragment at column 0 means the whole line" shortcut: it ate trailing comments and
            // made the same search behave differently on an indented line.
            var pos = FragmentPosition(raw, s, searchTrimmed.Length, j);
            var prefix = raw.Substring(0, pos);
            var suffix = raw.Substring(pos + s.Length);

            if (hasReplacement)
            {
                result.Add(prefix + replace[j].TrimStart());
                if (j == searchTrimmed.Length - 1)
                    lastSuffix = suffix;
            }
            else if (j == searchTrimmed.Length - 1 && searchTrimmed.Length > 1)
            {
                // Last boundary line without a replacement: the prefix is the leading
                // indentation — it is absorbed and the suffix joins the last emitted line
                // (legacy semantics, no whitespace-only lines are introduced).
                lastSuffix = suffix;
            }
            else
            {
                // First boundary line or single-line fragment without a replacement:
                // only the fragment is deleted — the rest of the line stays on its own line.
                var rest = prefix + suffix;
                if (rest.Trim().Length > 0)
                    result.Add(rest);
            }
        }

        // Extra replacement lines are inserted after the last matched line.
        for (var j = searchTrimmed.Length; j < replace.Count; j++)
            result.Add(replace[j]);

        // The trailing part of the last matched line stays attached to the last emitted
        // line (an extra replacement line, when present); when the whole matched block
        // was deleted, it attaches to the preceding text (or becomes the first line).
        if (lastSuffix != null)
        {
            if (result.Count > 0)
                result[result.Count - 1] += lastSuffix;
            else
                result.Add(lastSuffix);
        }

        for (var i = start + searchTrimmed.Length; i < target.Count; i++)
            result.Add(target[i]);

        target.Clear();
        target.AddRange(result);
    }

    /// <summary>
    /// Position of the matched fragment within the raw target line:
    /// single-line search — the leftmost occurrence (legacy semantics);
    /// first boundary line (suffix match) — the rightmost occurrence (the fragment ends the
    /// line); last boundary line (prefix match) — right after the leading indentation (the
    /// fragment starts the line).
    /// </summary>
    private static int FragmentPosition(string raw, string s, int searchCount, int j)
    {
        if (searchCount == 1)
            return raw.IndexOf(s, StringComparison.OrdinalIgnoreCase);
        if (j == 0)
            return raw.LastIndexOf(s, StringComparison.OrdinalIgnoreCase);
        return raw.Length - raw.TrimStart().Length;
    }

    /// <summary>
    /// Checks whether the search block matches the target lines starting at <paramref name="start"/>.
    /// </summary>
    private static bool LinesMatch(List<string> target, int start, string[] searchTrimmed)
    {
        for (var j = 0; j < searchTrimmed.Length; j++)
        {
            var t = target[start + j].Trim();
            var s = searchTrimmed[j];

            // Empty search line ↔ empty/whitespace-only target line.
            if (s.Length == 0)
            {
                if (t.Length != 0)
                    return false;
                continue;
            }

            if (searchTrimmed.Length == 1)
            {
                // Single-line search: substring match.
                if (t.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            else if (j == 0)
            {
                // First boundary line: may be truncated at the START (suffix match).
                if (!t.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else if (j == searchTrimmed.Length - 1)
            {
                // Last boundary line: may be truncated at the END (prefix match).
                if (!t.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else if (!string.Equals(t, s, StringComparison.OrdinalIgnoreCase))
            {
                // Middle lines must match exactly.
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether every search line of the block matches its target line exactly.
    /// </summary>
    private static bool IsWholeLineBlock(List<string> target, int start, string[] searchTrimmed)
    {
        for (var j = 0; j < searchTrimmed.Length; j++)
        {
            if (!string.Equals(target[start + j].Trim(), searchTrimmed[j], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}

/// <summary>What one <see cref="EditApplier.ApplyEdit"/> did — the model must see WHY nothing
/// happened, not just "-1".</summary>
public enum EditOutcome
{
    /// <summary>Something was replaced.</summary>
    Applied,

    /// <summary>The search block is not in the file.</summary>
    NotFound,

    /// <summary>The search block occurs in several places — refused instead of editing a guess.</summary>
    Ambiguous
}

/// <summary>
/// Outcome of one edit. <see cref="StartLine"/> is 0-based, -1 when nothing was applied;
/// <see cref="MatchLines"/> carries the 0-based start lines of every occurrence, so the caller can
/// tell the model exactly where an ambiguity is.
/// </summary>
public readonly struct EditApplyResult
{
    private EditApplyResult(EditOutcome outcome, int startLine, int appliedCount, IReadOnlyList<int> matchLines)
    {
        Outcome = outcome;
        StartLine = startLine;
        AppliedCount = appliedCount;
        MatchLines = matchLines;
    }

    public EditOutcome Outcome { get; }

    /// <summary>0-based line where the FIRST replacement landed; -1 when nothing was applied.</summary>
    public int StartLine { get; }

    /// <summary>How many blocks were replaced (0, 1, or more with `replaceAll`).</summary>
    public int AppliedCount { get; }

    /// <summary>
    /// 0-based start lines of every occurrence found, ascending — used by the ambiguity report
    /// and by the caller's diagnostics. An `Applied` outcome carries ALL matches, not just the
    /// one (or the many, with `replaceAll`) that were rewritten: the counts differ and the
    /// caller needs the positions to explain a refusal.
    /// </summary>
    public IReadOnlyList<int> MatchLines { get; }

    public int MatchCount => MatchLines.Count;

    internal static EditApplyResult Applied(int startLine, int appliedCount, IReadOnlyList<int> matchLines) =>
        new(EditOutcome.Applied, startLine, appliedCount, matchLines);

    internal static EditApplyResult NotFound() => new(EditOutcome.NotFound, -1, 0, []);

    internal static EditApplyResult Ambiguous(IReadOnlyList<int> matchLines) => new(EditOutcome.Ambiguous, -1, 0, matchLines);
}
