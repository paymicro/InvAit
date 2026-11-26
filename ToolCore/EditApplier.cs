namespace ToolCore;

/// <summary>
/// Line-based fuzzy search and replace for applying diff edits.
///
/// INPUT — <see cref="SplitEditText"/>, the ONE place where an edit text becomes a line list: CRLF, LF
/// and a lone CR are all line breaks; ONE trailing break is a terminator, not an extra empty line (an
/// oldStr copied from a numbered listing would otherwise search for an empty line that may not follow);
/// a SECOND trailing break is kept, because the model did address a trailing empty line. The applier
/// never sees a line ending — it works on an already-split list.
///
/// MATCHING — case-insensitive, search lines trimmed, occupying consecutive target lines:
/// the FIRST line of a block may be truncated at the START (suffix match), the LAST at the END (prefix
/// match), MIDDLE lines must match exactly; a SINGLE-line search matches as a substring; empty search
/// lines match empty target lines; an all-empty block matches the first empty target line. A hint
/// restricts the preferred range to [hint - 1 - tol, hint - 1 + tol], and when that window holds
/// nothing the whole candidate set is used again — several candidates left means REFUSAL, never a
/// silent first match.
///
/// REPLACEMENT — in place:
/// a fully matched line is replaced VERBATIM (the replacement brings its own indentation); a truncated
/// boundary line loses only the matched fragment, and the rest of the line survives — for the first
/// boundary line that fragment is the RIGHTMOST occurrence, and the trailing part of the last matched
/// line stays attached to the last emitted line (or to the preceding text when the block is deleted).
/// ANY substring match replaces only the substring, at column 0 included: `x();` inside
/// `x(); // needed by X` leaves the comment, which replacing the whole line used to eat.
/// A missing replacement line deletes what its search line matched — a whole line disappears, a
/// fragment-matched line loses only the fragment. A LONE whole line cannot be deleted here, because
/// `newStr: ""` splits to [""] and leaves an EMPTY line: use `create_file`, or a block whose
/// replacement has fewer lines than the search. Extra replacement lines go after the last matched
/// line, and an all-empty block replaces the matched empty lines with the replacement block.
///
/// AMBIGUITY — an edit matching several places is REFUSED unless the hint singles one out or
/// `replaceAll` is set. Silent first-of-many is how an edit lands in the wrong block and still reports
/// success.
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

        // An all-empty block is settled BEFORE `replaceAll` is looked at: it carries no text that could
        // disambiguate it. Letting `replaceAll` have it would rewrite EVERY blank line, because
        // `oldStr: "\n"` splits to one empty search line.
        if (IsAllEmpty(searchTrimmed))
        {
            var empty = FirstEmptyLine(matches, hint, tol);
            ApplyAt(target, empty, searchTrimmed, replace);
            return EditApplyResult.Applied(empty, 1, matches);
        }

        if (replaceAll)
        {
            // Bottom-to-top, so an applied replacement cannot shift a pending index. Overlapping
            // candidates are skipped: they are not distinct occurrences once the later one was rewritten.
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

        var selected = SelectSingle(target, matches, searchTrimmed, hint, tol);
        if (selected == null)
            return EditApplyResult.Ambiguous(matches);

        ApplyAt(target, selected.Value, searchTrimmed, replace);
        return EditApplyResult.Applied(selected.Value, 1, matches);
    }

    /// <summary>
    /// Picks the ONE occurrence to edit, or null when the choice would be a guess. Tie-breakers: the
    /// hint window narrows the set first (ignored when it holds nothing), then an exact whole-line
    /// block beats a fragment hit, then a single remaining candidate wins.
    /// </summary>
    private static int? SelectSingle(List<string> target, List<int> matches, string[] searchTrimmed, int hint, int tol)
    {
        // At least one candidate: `ApplyEdit` answers `NotFound` before asking for a selection.
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


    /// <summary>
    /// The one empty line an all-empty search settles on. NEAREST to the hint wins over "first in the
    /// window": several blank lines must not push the edit to the top of the file — the hint is the
    /// only thing that can place an empty search.
    /// </summary>
    private static int FirstEmptyLine(List<int> matches, int hint, int tol)
    {
        if (hint >= 0)
        {
            var inRange = InWindow(matches, hint, tol);
            if (inRange.Count > 0)
            {
                // A tie keeps the earlier line, so the answer stays deterministic.
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
    /// True when the trimmed search block carries no text at all. Both <see cref="SelectSingle"/> and
    /// <see cref="ApplyAt"/> give it a rule of its own, so the test has one definition.
    /// </summary>
    private static bool IsAllEmpty(string[] searchTrimmed) => searchTrimmed.All(s => s.Length == 0);

    /// <summary>
    /// Every start line where the trimmed search block matches, ascending. The count is what decides
    /// between "apply" and "refuse as ambiguous", so "find the first one" is not enough.
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
        // All search lines are empty: replace the matched empty LINES, every one of them.
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

            // Whole-line match: the replacement is used VERBATIM, with its own indentation. A missing
            // replacement DELETES the line.
            if (string.Equals(raw.Trim(), s, StringComparison.OrdinalIgnoreCase))
            {
                if (hasReplacement)
                    result.Add(replace[j]);
                continue;
            }

            // Fragment match: only the matched part is replaced, the rest of the line survives. There
            // is deliberately no "fragment at column 0 means the whole line" shortcut — it ate trailing
            // comments.
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
                // Last boundary line without a replacement: the prefix is indentation and is absorbed,
                // and the suffix joins the last emitted line.
                lastSuffix = suffix;
            }
            else
            {
                // Only the fragment is deleted; the rest of the line stays on its own line.
                var rest = prefix + suffix;
                if (rest.Trim().Length > 0)
                    result.Add(rest);
            }
        }

        // Extra replacement lines are inserted after the last matched line.
        for (var j = searchTrimmed.Length; j < replace.Count; j++)
            result.Add(replace[j]);

        // The trailing part of the last matched line joins the last emitted line, or becomes the first
        // line when the whole block was deleted.
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
    /// Position of the matched fragment in the raw line: single-line search — leftmost occurrence;
    /// first boundary line — rightmost (the fragment ends the line); last boundary line — right after
    /// the leading indentation.
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

/// <summary>What one <see cref="EditApplier.ApplyEdit"/> did — the model must see WHY nothing happened.</summary>
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
/// Outcome of one edit. <see cref="MatchLines"/> carries the 0-based start lines of every occurrence,
/// so the caller can tell the model exactly where an ambiguity is.
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
    /// 0-based start lines of every occurrence found, ascending — used by the ambiguity report. An
    /// `Applied` outcome carries ALL matches, not just the ones rewritten: the counts differ.
    /// </summary>
    public IReadOnlyList<int> MatchLines { get; }

    public int MatchCount => MatchLines.Count;

    internal static EditApplyResult Applied(int startLine, int appliedCount, IReadOnlyList<int> matchLines) =>
        new(EditOutcome.Applied, startLine, appliedCount, matchLines);

    internal static EditApplyResult NotFound() => new(EditOutcome.NotFound, -1, 0, []);

    internal static EditApplyResult Ambiguous(IReadOnlyList<int> matchLines) => new(EditOutcome.Ambiguous, -1, 0, matchLines);
}
