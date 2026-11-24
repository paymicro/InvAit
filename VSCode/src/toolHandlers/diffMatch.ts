/**
 * Diff matching utilities for edit_file tool.
 * Port from UniversalDiffParser.cs.
 */

/**
 * Checks if the target lines starting at startIdx match the search lines.
 * Comparison is case-insensitive and ignores leading/trailing whitespace.
 * Empty search lines match empty target lines only.
 */
export function isMatch(startIdx: number, target: string[], search: string[]): boolean {
    for (let j = 0; j < search.length; j++) {
        const targetLine = target[startIdx + j];
        const searchLine = search[j];
        const trimmedTarget = targetLine.trim();
        const trimmedSearch = searchLine.trim();
        if (trimmedSearch === '') {
            if (trimmedTarget !== '') return false;
            continue;
        }
        if (trimmedTarget.toLowerCase() !== trimmedSearch.toLowerCase()) return false;
    }
    return true;
}

/**
 * Splits an edit text (oldStr / newStr) into the line list consumed by findInFile.
 *
 * This is the ONE place where an edit text becomes a line list — mirrors
 * `EditApplier.SplitEditText` in ToolCore:
 * - CRLF, LF and a lone CR are all line breaks, so a mixed-ending file and a
 *   mixed-ending search block meet on the same list;
 * - ONE trailing line break is a terminator, not an extra empty line: "line2\n" is the
 *   one-line block "line2". Without this an oldStr copied from a numbered file listing
 *   searched for "line2" followed by an EMPTY line and failed on any file where the next
 *   line is not empty;
* - a SECOND trailing break is kept — the model really did address a trailing empty line.
 *
 * Non-string input (unvalidated JSON, e.g. `oldStr: 0`) is treated as empty text: throwing
 * here would escape `editFiles` and abort the WHOLE batch instead of one edit.
 */
export function splitEditText(text: string | null | undefined): string[] {
    if (typeof text !== 'string' || text.length === 0) {
        return [''];
    }

    const normalized = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    const lines = normalized.split('\n');
    if (lines.length > 1 && lines[lines.length - 1] === '') {
        lines.pop();
    }
    return lines;
}

/**
 * Finds the starting index of `search` within `target`.
 * Uses `hint` (1-based line number) as a starting point with `tol` tolerance.
 * Falls back to full file search if hint-based search fails.
 * Returns -1 if no match is found.
 */
export function findInFile(target: string[], search: string[], hint: number, tol: number): number {
    if (!target || !search) return -1;
    if (search.length === 0) return -1;
    if (search.length > target.length) return -1;
    if (tol < 0) tol = 0;

    if (hint !== -1) {
        // Single-line optimization: check exact hint first
        if (search.length === 1) {
            const exactCandidate = hint - 1;
            if (exactCandidate >= 0 && exactCandidate < target.length) {
                if (isMatch(exactCandidate, target, search)) return exactCandidate;
            }
        }
        for (let offset = -tol; offset <= tol; offset++) {
            const candidate = (hint - 1) + offset;
            if (candidate >= 0 && candidate <= target.length - search.length) {
                if (isMatch(candidate, target, search)) return candidate;
            }
        }
        // Fallback to full search
        return findInFile(target, search, -1, tol);
    }

    // Full file search
    for (let i = 0; i <= target.length - search.length; i++) {
        if (isMatch(i, target, search)) return i;
    }
    return -1;
}
