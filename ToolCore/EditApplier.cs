namespace ToolCore;

/// <summary>
/// Line-based fuzzy search and replace for applying diff edits.
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
/// [hint - 1 - tol, hint - 1 + tol] (1-based, inclusive) with a fallback to the first
/// match anywhere in the file.
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
/// - a single-line search whose fragment starts the line while the line continues after
///   it replaces the WHOLE line (the model cut the end of the line);
/// - a missing replacement line (replace shorter than search) DELETES the matched line;
/// - extra replacement lines are inserted after the last matched line;
/// - an all-empty search block replaces the matched empty line with the replacement block.
/// </summary>
public static class EditApplier
{
    /// <summary>
    /// Searches for occurrences of string list in target file.
    /// </summary>
    /// <param name="target">File lines to search in</param>
    /// <param name="search">Lines to search for</param>
    /// <param name="hint">1-based position hint (or -1 for full search)</param>
    /// <param name="tol">Offset tolerance from hint</param>
    /// <returns>0-based index of first match or -1 if not found</returns>
    public static int FindInFile(List<string>? target, List<string>? search, int hint, int tol)
    {
        if (target == null || search == null || search.Count == 0 || search.Count > target.Count)
            return -1;

        var searchTrimmed = search.Select(l => l.Trim()).ToArray();
        return FindMatchTrimmed(target, searchTrimmed, hint, tol) ?? -1;
    }

    /// <summary>
    /// Applies a single edit to the target lines in place: finds the search block
    /// (fuzzy, case-insensitive) and replaces it with the replace block.
    /// </summary>
    /// <param name="target">File lines (modified in place)</param>
    /// <param name="search">Lines to search for</param>
    /// <param name="replace">Replacement lines</param>
    /// <param name="hint">1-based position hint (or -1 for full search)</param>
    /// <param name="tol">Offset tolerance from hint</param>
    /// <returns>0-based index where the replacement was applied, or -1 if not found</returns>
    public static int ApplyEdit(List<string>? target, List<string>? search, List<string>? replace, int hint, int tol)
    {
        if (target == null || search == null || replace == null)
            return -1;
        if (search.Count == 0 || search.Count > target.Count)
            return -1;

        var searchTrimmed = search.Select(l => l.Trim()).ToArray();

        var start = FindMatchTrimmed(target, searchTrimmed, hint, tol);
        if (start == null)
            return -1;

        // All search lines are empty: replace the matched empty line with the replacement block.
        if (searchTrimmed.All(s => s.Length == 0))
        {
            target.RemoveAt(start.Value);
            for (var j = replace.Count - 1; j >= 0; j--)
                target.Insert(start.Value, replace[j]);
            return start.Value;
        }

        var result = new List<string>(target.Count);
        for (var i = 0; i < start.Value; i++)
            result.Add(target[i]);

        string? lastSuffix = null;
        for (var j = 0; j < search.Count; j++)
        {
            var raw = target[start.Value + j];
            var s = searchTrimmed[j];
            var hasReplacement = j < replace.Count;

            // Single-line search whose fragment starts the line while the line continues
            // after it: the model cut the end of the line — replace the WHOLE line.
            if (search.Count == 1 && raw.StartsWith(s, StringComparison.OrdinalIgnoreCase) && raw.Length > s.Length)
            {
                if (hasReplacement)
                    result.Add(replace[0]);
                continue;
            }

            // Whole-line match: the replacement is used VERBATIM (a complete line with its
            // own indentation, e.g. a re-nested block). A missing replacement DELETES the line.
            if (string.Equals(raw.Trim(), s, StringComparison.OrdinalIgnoreCase))
            {
                if (hasReplacement)
                    result.Add(replace[j]);
                continue;
            }

            // Fragment (truncated boundary line): only the matched part is removed/replaced;
            // the rest of the line (leading indentation and the trailing part) is preserved.
            var pos = FragmentPosition(raw, s, search.Count, j);
            var prefix = raw.Substring(0, pos);
            var suffix = raw.Substring(pos + s.Length);

            if (hasReplacement)
            {
                result.Add(prefix + replace[j].TrimStart());
                if (j == search.Count - 1)
                    lastSuffix = suffix;
            }
            else if (j == search.Count - 1 && search.Count > 1)
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
        for (var j = search.Count; j < replace.Count; j++)
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

        for (var i = start.Value + search.Count; i < target.Count; i++)
            result.Add(target[i]);

        target.Clear();
        target.AddRange(result);
        return start.Value;
    }

    /// <summary>
    /// Position of the matched fragment within the raw target line:
    /// single-line search — the leftmost occurrence (legacy semantics, consistent
    /// with FindInFile); first boundary line (suffix match) — the rightmost occurrence
    /// (the fragment ends the line); last boundary line (prefix match) — right after
    /// the leading indentation (the fragment starts the line).
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
    /// Finds the first match of the (trimmed) search block. When hint is given, prefers
    /// matches within the tolerance range around the hint line; otherwise prefers a match
    /// where every search line occupies its whole line (exact match) over a partial
    /// (fragment) match on an earlier line.
    /// </summary>
    /// <returns>0-based start line of the match, or null if not found.</returns>
    private static int? FindMatchTrimmed(List<string> target, string[] searchTrimmed, int hint, int tol)
    {
        if (searchTrimmed.Length > target.Count)
            return null;
        if (tol < 0)
            tol = 0;

        // All search lines are empty: match the first empty/whitespace-only line
        // (within the hint tolerance range when a hint is given).
        if (searchTrimmed.All(s => s.Length == 0))
            return FindEmptyLine(target, hint, tol);

        int? fallback = null;
        var minLine = hint < 0 ? 0 : Math.Max(0, hint - 1 - tol);
        var maxLine = hint < 0 ? int.MaxValue : hint - 1 + tol;

        for (var i = 0; i + searchTrimmed.Length <= target.Count; i++)
        {
            if (!LinesMatch(target, i, searchTrimmed))
                continue;

            fallback ??= i;

            if (hint >= 0)
            {
                // Hint-based search: the first match within the tolerance range around the
                // hint wins; if none, fall back to the first match anywhere in the file.
                if (i >= minLine && i <= maxLine)
                    return i;
            }
            else if (IsWholeLineBlock(target, i, searchTrimmed))
            {
                // Full search: prefer a match where every line occupies its whole line
                // (exact match) — a fragment match on an earlier line must not shadow it.
                return i;
            }
        }

        return fallback;
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

    /// <summary>0-based index of the first empty/whitespace-only line, or null.</summary>
    private static int? FindEmptyLine(List<string> target, int hint, int tol)
    {
        if (hint < 0)
        {
            var idx = target.FindIndex(string.IsNullOrWhiteSpace);
            return idx < 0 ? null : idx;
        }

        var min = Math.Max(0, hint - 1 - tol);
        var max = hint - 1 + tol;
        for (var i = min; i <= max && i < target.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(target[i]))
                return i;
        }

        // Fall back to the first empty line anywhere.
        var fallback = target.FindIndex(string.IsNullOrWhiteSpace);
        return fallback < 0 ? null : fallback;
    }
}
