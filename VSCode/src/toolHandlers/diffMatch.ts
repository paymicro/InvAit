/**
 * Diff matching utilities for edit_files tool.
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
