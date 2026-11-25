/**
 * Fuzzy line matching and replacement for the `edit_file` tool.
 *
 * Port of `ToolCore/EditApplier.cs`. The two hosts share no code, so the RULES live twice:
 * change them TOGETHER, or an edit lands on different lines in the VS host and in VS Code
 * while both of them report success.
 *
 * MATCHING (`linesMatch`) — case-insensitive; search lines are trimmed, so their own
 * indentation is ignored; search lines must occupy consecutive target lines:
 * - the FIRST line of a multi-line block may be truncated at the START (suffix match);
 * - the LAST line may be truncated at the END (prefix match);
 * - MIDDLE lines must match exactly;
 * - a SINGLE-line search matches as a SUBSTRING of the target line;
 * - empty/whitespace-only search lines match empty/whitespace-only target lines.
 *
 * REPLACEMENT (`applyEdit` -> `applyAt`):
 * - a fully matched line is replaced VERBATIM by its replacement line (the replacement keeps
 *   its own indentation, e.g. a re-nested block);
 * - a truncated boundary line loses/receives only the matched fragment: leading indentation and
 *   the trailing part of the line survive (`prefix + replace.trimStart()` + suffix);
 * - a single-line search whose fragment STARTS the line KEEPS the rest of the line: the model may
 *   have cut the line's tail, but a column-0 fragment is not a whole line, and dropping the tail
 *   here made the result depend on the target's indentation (behaviour pin, not an accident: see
 *   `ApplyEdit_SearchEndingWithNewLine_FragmentAtColumnZero_KeepsTrailingComment` in
 *   ToolCore.Tests/EditApplierTests.cs);
 * - a missing replacement line (replace shorter than search) DELETES the matched line;
 * - extra replacement lines are inserted after the last matched line;
 * - the trailing part of the last matched line stays attached to the last emitted line (an extra
 *   replacement line, when present); when the whole matched block is deleted it attaches to the
 *   preceding text, or becomes the first line if the block starts the file;
 * - an all-empty search block replaces the matched `search.length` lines with the replacement
 *   block (removing `search.length`, not one: the C# host is pinned to the same rule).
 *
 * AMBIGUITY (`selectEditTarget`) — an edit that matches several places is REFUSED, not applied to
 * the first one, unless a hint singles one out or `replaceAll` is set. Silently replacing the
 * first of many is how an edit lands in the wrong block with a success message.
 */

/** The one occurrence to edit, plus every occurrence found. */
export interface EditSelection {
    /** All occurrences found (0-based start lines, ascending). */
    matches: number[];
    /** The occurrence to edit, or null when the choice would be a guess. */
    selected: number | null;
}

/** What one `applyEdit` did — the caller must see WHY nothing happened, not just "-1". */
export interface EditApplyOutcome {
    outcome: 'applied' | 'notFound' | 'ambiguous';
    /** 0-based line where the FIRST replacement landed; -1 when nothing was applied. */
    startLine: number;
    /** How many blocks were replaced (0, 1, or more with `replaceAll`). */
    appliedCount: number;
    /** 0-based start lines of every occurrence found (empty when nothing matched). */
    matchLines: number[];
}

/**
 * Splits an edit text (oldStr / newStr) into the line list consumed by `applyEdit`.
 *
 * This is the ONE place where an edit text becomes a line list — mirrors
 * `EditApplier.SplitEditText` in ToolCore:
 * - CRLF, LF and a lone CR are all line breaks, so a mixed-ending file and a
 *   mixed-ending search block meet on the same list;
 * - ONE trailing line break is a terminator, not an extra empty line: "line2\n" is the
 *   one-line block "line2". Without this an oldStr copied from a numbered file listing
 *   searched for "line2" followed by an EMPTY line and failed on any file where the next line
 *   is not empty;
 * - a SECOND trailing break is kept — the model really did address a trailing empty line.
 *
 * Non-string input (unvalidated JSON, e.g. `oldStr: 0`) is treated as empty text: throwing
 * here would escape `editFiles` and abort the WHOLE batch instead of one edit. null/undefined
 * are rejected by the CALLER before matching — an empty search legitimately matches the first
 * empty line, so "oldStr is missing" must never reach this function's output.
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
 * Checks if the target lines starting at `startIdx` match the (already trimmed) search lines.
 * Verbatim port of `EditApplier.LinesMatch` — see the file header for the rules.
 */
export function linesMatch(target: string[], startIdx: number, searchTrimmed: string[]): boolean {
    for (let j = 0; j < searchTrimmed.length; j++) {
        const targetLine = target[startIdx + j].trim();
        const searchLine = searchTrimmed[j];

        // Empty search line <-> empty/whitespace-only target line.
        if (searchLine.length === 0) {
            if (targetLine.length !== 0) return false;
            continue;
        }

        if (searchTrimmed.length === 1) {
            // Single-line search: substring match.
            if (!containsIgnoreCase(targetLine, searchLine)) return false;
        } else if (j === 0) {
            // First boundary line: may be truncated at the START (suffix match).
            if (!endsWithIgnoreCase(targetLine, searchLine)) return false;
        } else if (j === searchTrimmed.length - 1) {
            // Last boundary line: may be truncated at the END (prefix match).
            if (!startsWithIgnoreCase(targetLine, searchLine)) return false;
        } else if (!equalsIgnoreCase(targetLine, searchLine)) {
            // Middle lines must match exactly.
            return false;
        }
    }

    return true;
}

/**
 * Checks whether every search line of the block matches its target line EXACTLY (no fragment,
 * no truncation). Port of `EditApplier.IsWholeLineBlock`: tie-breaker #4 of the selection.
 */
export function isWholeLineBlock(target: string[], startIdx: number, searchTrimmed: string[]): boolean {
    for (let j = 0; j < searchTrimmed.length; j++) {
        if (!equalsIgnoreCase(target[startIdx + j].trim(), searchTrimmed[j])) return false;
    }
    return true;
}

/**
 * Every start index where the (already trimmed) `searchTrimmed` matches, ascending. Port of
 * `EditApplier.FindAllMatches`, down to the contract: the CALLER trims once and passes the
 * trimmed block, so this function never re-trims. The COUNT is what decides between "apply" and
 * "refuse as ambiguous", so "find the first one" is not enough here.
 */
function findAllMatches(target: string[], searchTrimmed: string[]): number[] {
    const matches: number[] = [];
    if (!target || !searchTrimmed || searchTrimmed.length === 0 || searchTrimmed.length > target.length) return matches;

    for (let i = 0; i + searchTrimmed.length <= target.length; i++) {
        if (linesMatch(target, i, searchTrimmed)) matches.push(i);
    }
    return matches;
}

/**
 * Picks the ONE occurrence to edit, or null when the choice is a guess. Port of
 * `EditApplier.SelectSingle`, shared by `selectEditTarget` and `applyEdit` so the two entry
 * points cannot drift apart.
 *
 * Order of the tie-breakers:
 * 1. an all-empty search has no text to disambiguate with and is never ambiguous — the first blank
 *    line inside the hint window nearest to the hint, else the first one in the file
 *    (`firstEmptyLine`);
 * 2. a single occurrence wins;
 * 3. the hint window (1-based `hint` +/- `tol`) narrows the set;
 * 4. among the candidates an exact whole-line block beats a fragment (substring) hit;
 * 5. anything left is ambiguous: the caller must REFUSE, not replace the first candidate.
 *
 * Rule 1 stays here because `selectEditTarget` has no other place for it. `applyEdit` settles the
 * same case BEFORE its `replaceAll` branch (the flag must not decide the scope of an empty pattern),
 * so the two entry points answer identically and the C# twin — whose only caller guards first —
 * needs no copy.
 */
function selectSingle(target: string[], matches: number[], searchTrimmed: string[], hint: number, tol: number): number | null {
    if (matches.length === 0) return null;

    // An empty search cannot be clarified by adding context: hinted line, else the first one.
    if (searchTrimmed.every(line => line.length === 0)) {
        return firstEmptyLine(matches, hint, tol);
    }

    if (matches.length === 1) return matches[0];

    let candidates = matches;
    if (hint >= 0) {
        const inRange = inWindow(matches, hint, tol);
        if (inRange.length > 0) candidates = inRange;
    }

    if (candidates.length === 1) return candidates[0];

    const wholeLine = candidates.filter(m => isWholeLineBlock(target, m, searchTrimmed));
    return wholeLine.length === 1 ? wholeLine[0] : null;
}

/** Match indices whose 0-based start line falls into the hint's tolerance window. */
function inWindow(matches: number[], hint: number, tol: number): number[] {
    const tolerance = Math.max(0, tol);
    const minLine = Math.max(0, hint - 1 - tolerance);
    const maxLine = hint - 1 + tolerance;
    return matches.filter(m => m >= minLine && m <= maxLine);
}

/**
 * The one empty line an all-empty search settles on: the FIRST candidate inside the hint window,
 * otherwise the first one in the file.
 *
 * NOT the "is the choice unambiguous" test that `selectSingle` performs: an empty search is never
 * ambiguous, and several blank lines inside the window must not push the edit to the top of the
 * file — which is what a `singleMatchInWindow(…) ?? matches[0]` chain did. The hint is the only
 * thing that can place an empty search, so it has to win. Mirrors `EditApplier.FirstEmptyLine`.
 */
function firstEmptyLine(matches: number[], hint: number, tol: number): number {
    if (hint >= 0) {
        const inRange = inWindow(matches, hint, tol);
        if (inRange.length > 0) {
            // NEAREST to the hint, not the first in the window — mirror of
            // `EditApplier.FirstEmptyLine`: ±5 lines is wide enough for the earliest blank line to
            // be one the model never pointed at, and the edit still reported success.
            let target = inRange[0];
            for (const candidate of inRange) {
                if (Math.abs(candidate - (hint - 1)) < Math.abs(target - (hint - 1))) target = candidate;
            }
            return target;
        }
    }

    return matches[0];
}

/**
 * Finds the occurrences of `search` in `target` and picks the one to edit.
 * `search` is the raw (untrimmed) output of `splitEditText`; `hint` is the 1-based
 * `approximateLine` or -1 for a full search.
 */
export function selectEditTarget(target: string[], search: string[], hint: number, tol: number): EditSelection {
    const searchTrimmed = trimAll(search);
    const matches = findAllMatches(target, searchTrimmed);
    return { matches, selected: selectSingle(target, matches, searchTrimmed, hint, tol) };
}

/**
 * Applies ONE edit to `target` IN PLACE: finds the search block (fuzzy, case-insensitive) and
 * replaces it with the replace block. Port of `EditApplier.ApplyEdit`.
 *
 * AMBIGUITY IS A REFUSAL: when the block occurs in several places and nothing disambiguates it,
 * nothing is applied and the caller gets `ambiguous` with every candidate line.
 */
export function applyEdit(
    target: string[],
    search: string[],
    replace: string[],
    hint: number,
    tol: number,
    replaceAll: boolean = false,
): EditApplyOutcome {
    if (!target || !search || !replace) return notFound();
    if (search.length === 0 || search.length > target.length) return notFound();

    const searchTrimmed = trimAll(search);
    const matches = findAllMatches(target, searchTrimmed);
    if (matches.length === 0) return notFound();

    // An all-empty search block is settled FIRST, BEFORE `replaceAll` is looked at. It carries no
    // text that could disambiguate it, so the legacy rule stands: the hinted line if any, otherwise
    // the FIRST empty line. Letting `replaceAll` have it would rewrite EVERY blank line in the file
    // — `oldStr: "\n"` (a very natural spelling of "a blank line") splits to one empty search line,
    // which changes the scope from one line to the whole file. The C# twin carries the SAME guard in
    // `EditApplier.ApplyEdit`; the two must not be allowed to disagree about the scope of an empty
    // pattern (see docs/dev/edit-matching.md).
    if (searchTrimmed.every(line => line.length === 0)) {
        const empty = firstEmptyLine(matches, hint, tol);
        applyAt(target, empty, searchTrimmed, replace);
        return { outcome: 'applied', startLine: empty, appliedCount: 1, matchLines: matches };
    }

    if (replaceAll) {
        // Bottom-to-top so an already-applied replacement cannot shift a pending index. Overlapping
        // candidates (a single-line search inside a repeated block) are skipped: they are not
        // distinct occurrences any more once the later one was rewritten.
        let applied = 0;
        let firstLine = -1;
        let boundary = Number.MAX_SAFE_INTEGER;
        for (let k = matches.length - 1; k >= 0; k--) {
            const match = matches[k];
            if (match + searchTrimmed.length > boundary) continue;

            applyAt(target, match, searchTrimmed, replace);
            boundary = match;
            firstLine = match;
            applied++;
        }

        return { outcome: 'applied', startLine: firstLine, appliedCount: applied, matchLines: matches };
    }

    const selected = selectSingle(target, matches, searchTrimmed, hint, tol);
    if (selected === null) {
        return { outcome: 'ambiguous', startLine: -1, appliedCount: 0, matchLines: matches };
    }

    applyAt(target, selected, searchTrimmed, replace);
    return { outcome: 'applied', startLine: selected, appliedCount: 1, matchLines: matches };
}

function notFound(): EditApplyOutcome {
    return { outcome: 'notFound', startLine: -1, appliedCount: 0, matchLines: [] };
}

/** Replaces the already-located block at `start` (in place). Port of `EditApplier.ApplyAt`. */
function applyAt(target: string[], start: number, searchTrimmed: string[], replace: string[]): void {
    // All search lines are empty: replace the matched line(s) with the replacement block.
    // `searchTrimmed.length` lines are removed, not one — a two-empty-line search consumes two.
    if (searchTrimmed.every(s => s.length === 0)) {
        target.splice(start, searchTrimmed.length, ...replace);
        return;
    }

    const result: string[] = target.slice(0, start);
    let lastSuffix: string | null = null;

    for (let j = 0; j < searchTrimmed.length; j++) {
        const raw = target[start + j];
        const s = searchTrimmed[j];
        const hasReplacement = j < replace.length;

        // NO special case for a fragment that happens to START the line: replacing `x();` in
        // `x(); // keep` must leave ` // keep` alone. Cutting the tail here made the result depend
        // on the target's indentation (column 0 lost the tail, an indented line kept it) — the
        // fragment path below replaces only the matched part in both cases.

        // Whole-line match: the replacement is used VERBATIM (a complete line with its own
        // indentation, e.g. a re-nested block). A missing replacement DELETES the line.
        if (equalsIgnoreCase(raw.trim(), s)) {
            if (hasReplacement) result.push(replace[j]);
            continue;
        }

        // Fragment (truncated boundary line): only the matched part is removed/replaced; the rest
        // of the line (leading indentation and the trailing part) is preserved.
        const pos = fragmentPosition(raw, s, searchTrimmed.length, j);
        if (pos < 0) {
            // Defensive: the fragment cannot be located in the raw line (a case mapping that
            // changes length). Keep the line untouched instead of corrupting it with a negative
            // slice, and never throw — an exception here would abort the whole edit batch.
            result.push(raw);
            continue;
        }

        const prefix = raw.slice(0, pos);
        const suffix = raw.slice(pos + s.length);

        if (hasReplacement) {
            result.push(prefix + replace[j].trimStart());
            if (j === searchTrimmed.length - 1) lastSuffix = suffix;
        } else if (j === searchTrimmed.length - 1 && searchTrimmed.length > 1) {
            // Last boundary line without a replacement: the prefix is the leading indentation —
            // it is absorbed and the suffix joins the last emitted line (legacy semantics, no
            // whitespace-only lines are introduced).
            lastSuffix = suffix;
        } else if ((prefix + suffix).trim().length > 0) {
            // First boundary line or single-line fragment without a replacement: only the
            // fragment is deleted — the rest of the line stays on its own line.
            result.push(prefix + suffix);
        }
    }

    // Extra replacement lines are inserted after the last matched line.
    for (let j = searchTrimmed.length; j < replace.length; j++) {
        result.push(replace[j]);
    }

    // The trailing part of the last matched line stays attached to the last emitted line (an
    // extra replacement line, when present); when the whole matched block was deleted, it
    // attaches to the preceding text (or becomes the first line).
    if (lastSuffix !== null) {
        if (result.length > 0) result[result.length - 1] += lastSuffix;
        else result.push(lastSuffix);
    }

    for (let i = start + searchTrimmed.length; i < target.length; i++) {
        result.push(target[i]);
    }

    target.length = 0;
    for (const line of result) target.push(line);
}

/**
 * Position of the matched fragment within the raw target line. Port of
 * `EditApplier.FragmentPosition`:
 * single-line search — the leftmost occurrence; first boundary line (suffix match) — the
 * rightmost occurrence (the fragment ends the line); last boundary line (prefix match) — right
 * after the leading indentation (the fragment starts the line).
 */
function fragmentPosition(raw: string, s: string, searchCount: number, j: number): number {
    if (searchCount === 1) return indexOfIgnoreCase(raw, s, false);
    if (j === 0) return indexOfIgnoreCase(raw, s, true);
    return raw.length - raw.trimStart().length;
}

/** Trims every search line once (the comparison functions expect pre-trimmed lines). */
function trimAll(search: string[]): string[] {
    return search.map(line => line.trim());
}

// ---------------------------------------------------------------------------
// Case-insensitive primitives.
//
// C# uses `StringComparison.OrdinalIgnoreCase`; the closest portable equivalent is comparing
// lower-cased slices of the SAME length, which keeps every returned index valid for the
// ORIGINAL string (a whole-string lower-case can change lengths and shift indices).
// ---------------------------------------------------------------------------

function equalsIgnoreCase(a: string, b: string): boolean {
    return a.toLowerCase() === b.toLowerCase();
}

function startsWithIgnoreCase(haystack: string, needle: string): boolean {
    if (needle.length === 0) return true;
    if (haystack.length < needle.length) return false;
    return equalsIgnoreCase(haystack.slice(0, needle.length), needle);
}

function endsWithIgnoreCase(haystack: string, needle: string): boolean {
    if (needle.length === 0) return true;
    if (haystack.length < needle.length) return false;
    return equalsIgnoreCase(haystack.slice(haystack.length - needle.length), needle);
}

function containsIgnoreCase(haystack: string, needle: string): boolean {
    return indexOfIgnoreCase(haystack, needle, false) >= 0;
}

/** Index of `needle` in `haystack` (leftmost, or rightmost when `fromEnd`), or -1. */
function indexOfIgnoreCase(haystack: string, needle: string, fromEnd: boolean): number {
    if (needle.length === 0) return 0;
    const last = haystack.length - needle.length;
    if (last < 0) return -1;
    if (fromEnd) {
        for (let i = last; i >= 0; i--) {
            if (equalsIgnoreCase(haystack.slice(i, i + needle.length), needle)) return i;
        }
    } else {
        for (let i = 0; i <= last; i++) {
            if (equalsIgnoreCase(haystack.slice(i, i + needle.length), needle)) return i;
        }
    }
    return -1;
}
