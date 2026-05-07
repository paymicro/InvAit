namespace ToolCore;

/// <summary>
/// Universal parser for finding string matches in file content.
/// Used for applying diff changes.
/// </summary>
public static class UniversalDiffParser
{
    /// <summary>
    /// Checks if strings match starting from given index.
    /// </summary>
    /// <param name="startIdx">Start index in target</param>
    /// <param name="target">File lines</param>
    /// <param name="search">Lines to search for</param>
    /// <returns>true if all lines match</returns>
    private static bool IsMatch(int startIdx, List<string> target, List<string> search)
    {
        for (var j = 0; j < search.Count; j++)
        {
            var targetLine = target[startIdx + j];
            var searchLine = search[j];

            // Trim whitespace for comparison to handle minor formatting differences
            // but preserve the fact that both must have content
            var trimmedTarget = targetLine.Trim();
            var trimmedSearch = searchLine.Trim();

            // If search line is empty, match any empty/whitespace-only target line
            if (string.IsNullOrEmpty(trimmedSearch))
            {
                if (!string.IsNullOrEmpty(trimmedTarget))
                    return false;
                continue;
            }

            // Use OrdinalIgnoreCase for case-insensitive comparison
            if (!string.Equals(trimmedTarget, trimmedSearch, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Searches for occurrences of string list in target file.
    /// </summary>
    /// <param name="target">File lines to search in</param>
    /// <param name="search">Lines to search for</param>
    /// <param name="hint">1-based position hint (or -1 for full search)</param>
    /// <param name="tol">Offset tolerance from hint</param>
    /// <returns>0-based index of first match or -1 if not found</returns>
    public static int FindInFile(List<string> target, List<string> search, int hint, int tol)
    {
        // Null protection
        if (target == null || search == null)
            return -1;

        // Empty search protection
        if (search.Count == 0)
            return -1;

        // Search longer than target protection
        if (search.Count > target.Count)
            return -1;

        // Negative tolerance protection
        if (tol < 0)
            tol = 0;

        // 1. Hint-based search (1-based)
        if (hint != -1)
        {
            // Optimization: for single-line search, check exact hint first
            if (search.Count == 1)
            {
                var exactCandidate = hint - 1;
                if (exactCandidate >= 0 && exactCandidate < target.Count)
                {
                    if (IsMatch(exactCandidate, target, search))
                        return exactCandidate;
                }
            }

            for (var offset = -tol; offset <= tol; offset++)
            {
                var candidate = (hint - 1) + offset; // Convert 1-based hint to 0-based index
                if (candidate >= 0 && candidate <= target.Count - search.Count)
                {
                    if (IsMatch(candidate, target, search))
                    {
                        return candidate; // Return 0-based index
                    }
                }
            }
            // If not found in tolerance range, do full search
            return FindInFile(target, search, -1, tol);
        }

        // 2. Full file search
        for (var i = 0; i <= target.Count - search.Count; i++)
        {
            if (IsMatch(i, target, search))
            {
                return i;
            }
        }

        return -1;
    }
}
