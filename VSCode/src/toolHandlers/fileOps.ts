/**
 * File operation tools: read_files, create_file, edit_file, delete_file, dir.
 */

import * as fs from 'fs';
import * as path from 'path';
import { ToolResult } from './types';
import { getAbsolutePath, makeRelative, listDir } from './fileUtils';
import { applyEdit, splitEditText } from './diffMatch';
import { readFileWithMetadata, writeFileWithMetadata, separatorName } from './fileReader';
import { writeFileWithMode, parseMode } from './fileWriter';

/** How much of an offending value is echoed back. A whole 40 KB oldStr is not a message. */
const MAX_LITERAL_LENGTH = 60;

/**
 * The range of the C# `int` the hint ends up in. A number outside it cannot be a line number in the
 * sibling host, so accepting it here would run an edit the VS host refuses.
 */
const INT32_MIN = -2147483648;
const INT32_MAX = 2147483647;

/**
 * The offending value as the model sent it, cut to a readable length — `""` and `"yes"` must be
 * distinguishable at a glance, because "received a string" left the model guessing WHICH string.
 *
 * Mirrors `EditPayload.Describe` in the C# host: one sentence for one refusal, in two hosts. The
 * JSON form is reused verbatim so a string keeps its own quotes and a number its own spelling.
 */
function describeValue(value: unknown): string {
    if (value === undefined) {
        return 'nothing (the value is absent)';
    }
    if (value === null) {
        return 'null';
    }
    if (Array.isArray(value)) {
        return `an array ${shortLiteral(JSON.stringify(value))}`;
    }

    switch (typeof value) {
        case 'string':
            return `the string ${shortLiteral(JSON.stringify(value))}`;
        case 'number':
            // `JSON.stringify(Infinity)` is the string "null", so a refused `1e400` used to be reported
            // as `the number null` — naming a value the model never sent, in the one field the whole
            // sentence hinges on. The C# host echoes the raw `1e400` here; JS keeps the value itself.
            return `the number ${Number.isFinite(value) ? shortLiteral(JSON.stringify(value)) : String(value)}`;
        case 'boolean':
            return `the boolean ${String(value)}`;
        case 'object':
            return `an object ${shortLiteral(JSON.stringify(value))}`;
        default:
            return `${typeof value} ${shortLiteral(String(value))}`;
    }
}

/**
 * Marks a cut literal so the model cannot mistake a fragment for the whole value.
 *
 * The cut backs off one character when it would land between the two halves of a surrogate pair:
 * a lone surrogate is not the text of anything, and raw JSON of an emoji is exactly where a 60th
 * character lands inside one.
 *
 * A cut JSON STRING gets its closing quote back, exactly like `JsonShape.Short` does in the C# host:
 * the text is cut blind, so a long string used to arrive as `"abcdef\u2026` — an opening quote with no
 * end, in the one field the whole sentence hinges on.
 *
 * KNOWN LIMITATION against the C# host: `JsonShape.Describe` echoes the RAW JSON text, so `1e3`
 * reads `the number 1e3` there and `the number 1000` here, and an object keeps its original spacing
 * there and loses it here. JavaScript cannot recover the source text after `JSON.parse` — the same
 * class of limit as the number spelling — so the two hosts agree on the sentence and on WHICH value
 * is named, but not on the literal spelling of a number or the whitespace inside an object.
 */
function shortLiteral(text: string): string {
    if (text.length <= MAX_LITERAL_LENGTH) {
        return text;
    }

    let cut = MAX_LITERAL_LENGTH;
    const last = text.charCodeAt(cut - 1);
    if (last >= 0xd800 && last <= 0xdbff) {
        cut--;
    }

    const closing = text.startsWith('"') ? '"' : '';
    return `${text.slice(0, cut)}\u2026${closing}`;
}

/**
 * A property by name, IGNORING CASE — the lookup the VS host performs in `JsonShape.TryGetProperty`,
 * the one `System.Text.Json` used to perform with `PropertyNameCaseInsensitive = true`, and the one
 * the approval card performs with `OrdinalIgnoreCase`.
 *
 * Dropping that tolerance would be a real regression, not a tidy-up: `{"OldStr": …}` bound fine
 * before, so the card rendered a valid diff, the user APPROVED it, and the host answered
 * "oldStr is missing" — a refusal about a typo nobody made. `read_files` needs it too, and one copy
 * is what keeps `{"Path": "a.cs"}` working on both hosts.
 */
function readProperty(obj: any, name: string): any {
    if (obj === null || typeof obj !== 'object') {
        return undefined;
    }
    if (name in obj) {
        return obj[name];
    }

    const wanted = name.toLowerCase();
    for (const key of Object.keys(obj)) {
        if (key.toLowerCase() === wanted) {
            return obj[key];
        }
    }

    return undefined;
}

// 1. read_files
export function readFiles(params: any, workspaceRoot: string): ToolResult {
    const raw = params.files;

    // `files` itself: absent, `null` and a non-array are three DIFFERENT mistakes and none of them is
    // "no files". All three used to become `[]` and answer `{"success":true,"payload":"[]"}`, i.e. a
    // model that asked for four files was told it had read all of them. Every sentence below is the
    // one `ToolCore/ReadFilesPayload.cs` produces — `read_files` has the same kind of shape pass as
    // `edits`, so the two hosts answer the same payload with the same words.
    if (raw === undefined || raw === null) {
        return {
            success: false,
            error: "Failed to read 'files': the value is missing. Send a JSON array of reads, " +
                'e.g. [{"path": "src/File.cs"}]. Nothing was read.',
        };
    }
    if (!Array.isArray(raw)) {
        return {
            success: false,
            error: `Failed to read 'files': expected a JSON array, received ${describeValue(raw)}. Nothing was read.`,
        };
    }

    const files: any[] = raw;
    const results: any[] = [];

    if (files.length === 0) {
        return { success: false, error: 'No files specified.' };
    }

    // `null`, `-1` and `0` all mean "no range" — the schema offers ["integer","null"], and a
    // non-positive count is the older spelling of the sentinel. One number, decided ONCE, so the
    // dedup key and the arithmetic cannot disagree, and so this host agrees with the C# one, which
    // normalises the same three spellings in ToolCore/ReadFilesPayload.TryParse.
    //
    // Any OTHER value is a REFUSAL, not a silent "no range". Folding `1.5`, `"3"` or `3000000000`
    // to -1 here answered a request the sibling host rejected — and answered it with the ENTIRE
    // file, i.e. with far more text than was asked for. Same rule as the `edits` hint: refuse,
    // never round. `118.0` is a whole number and both hosts accept it (`JsonShape.ReadIntegerOrNull`
    // reads the same `decimal` here); the reason list is what has to stay identical, not the
    // spelling that produced it.
    const parseRange = (
        value: unknown,
        field: string,
        entryNo: string,
    ): { ok: true; value: number } | { ok: false; reason: string } => {
        if (value === undefined || value === null) {
            return { ok: true, value: -1 };
        }
        if (typeof value === 'number' && Number.isInteger(value)) {
            if (value > 2147483647 || value < -2147483648) {
                return {
                    ok: false,
                    reason: `${entryNo}: '${field}' is outside the 32-bit integer range, received ${describeValue(value)}. Nothing was read.`,
                };
            }
            return { ok: true, value: value > 0 ? value : -1 };
        }
        return {
            ok: false,
            reason: `${entryNo}: '${field}' must be an integer or null, received ${describeValue(value)}. Nothing was read.`,
        };
    };

    // One pass over the WHOLE array, before anything is resolved or read — the order the `edits`
    // shape pass below uses, and for the same reason: a refusal has to name the entry, so it cannot
    // come from the middle of a loop that has already answered half the request. An entry that
    // cannot yield a path or a range refuses the call; skipping it would hand back a shorter answer
    // than the one that was asked for, which is the kind of silence this function has to avoid.
    type ReadPlan = { path: string; startLine: number; lineCount: number };
    const planned: ReadPlan[] = [];
    for (let i = 0; i < files.length; i++) {
        const entryNo = `entry ${i + 1} of ${files.length}`;
        const f = files[i];

        // `files: [null]` is valid JSON. Reading `f.path` here threw `Cannot read properties of null`
        // — the same class of host-internals text the C# host stopped producing for this payload.
        if (f === null || typeof f !== 'object' || Array.isArray(f)) {
            return {
                success: false,
                error: `Failed to read 'files': ${entryNo} must be a JSON object with a 'path', received ${describeValue(f)}. Nothing was read.`,
            };
        }
        if (typeof readProperty(f, 'path') !== 'string' || readProperty(f, 'path').trim() === '') {
            return {
                success: false,
                error: `Failed to read 'files': ${entryNo} has no 'path'. Each entry needs a 'path' string. Nothing was read.`,
            };
        }

        const startLine = parseRange(readProperty(f, 'startLine'), 'startLine', entryNo);
        if (!startLine.ok) {
            return { success: false, error: `Failed to read 'files': ${startLine.reason}` };
        }
        const lineCount = parseRange(readProperty(f, 'lineCount'), 'lineCount', entryNo);
        if (!lineCount.ok) {
            return { success: false, error: `Failed to read 'files': ${lineCount.reason}` };
        }

        planned.push({ path: readProperty(f, 'path'), startLine: startLine.value, lineCount: lineCount.value });
    }

    // Deduplicate by the WHOLE request, not by path alone: two ranges of one file are two
    // different reads, and the old key silently dropped the second one.
    const seen = new Set<string>();
    const deduped: ReadPlan[] = [];
    for (const f of planned) {
        const key = `${f.path.toLowerCase()}|${f.startLine}|${f.lineCount}`;
        if (!seen.has(key)) {
            seen.add(key);
            deduped.push(f);
        }
    }

    for (const f of deduped) {
        const p: string = f.path;
        const startLine: number = f.startLine;
        const lineCount: number = f.lineCount;

        try {
            const absPath = getAbsolutePath(p, workspaceRoot);
            if (!fs.existsSync(absPath)) {
                results.push({ path: p, error: "File doesn't exist." });
                continue;
            }

            const content = fs.readFileSync(absPath, 'utf-8');
            let lines = content.split(/\r\n|\r|\n/);
            if (lines.length > 0 && lines[lines.length - 1] === '') {
                lines.pop();
            }

            const skipCount = startLine > 0 ? Math.max(0, startLine - 1) : 0;
            // `needsLimit` IS `lineCount <= 0`, so `lineCount > 0 ? lineCount : (needsLimit ? 2000 :
            // Infinity)` had an unreachable third arm — inside the false branch of `lineCount > 0`
            // the flag is always true and `Infinity` could never be produced. The C# host carries the
            // same simplification.
            const needsLimit = lineCount <= 0;
            const takeCount = needsLimit ? 2000 : lineCount;
            const totalLineCount = needsLimit ? lines.length : 0;

            const sliced = lines.slice(skipCount, skipCount + takeCount);

            const entry: any = { path: p, lines: sliced };

            if (skipCount > 0) {
                entry.startLine = skipCount + 1;
            }

            if (needsLimit && totalLineCount > 2000 && skipCount + sliced.length < totalLineCount) {
                entry.totalLines = totalLineCount;
            }

            results.push(entry);
        } catch (e: any) {
            results.push({ path: p, error: e.message ?? String(e) });
        }
    }

    return { success: true, payload: JSON.stringify(results) };
}

// 2. create_file
export function createFile(params: any, workspaceRoot: string): ToolResult {
    try {
        const filePath: string = params.filePath;
        // An ABSENT `content` and an EMPTY one are different things and must not be glued together:
        // `overwrite` deliberately does NOT treat an empty body as a no-op (creating an empty file is
        // legal), so coercing a missing field to '' truncated an existing file to zero bytes and then
        // reported "overwritten successfully". Same refusal as `newStr: null` in `edit_file`, and the
        // same sentence as `ToolExecutor.CreateNewFileAsync` in the VS host.
        if (params.content === undefined || params.content === null) {
            return {
                success: false,
                error: "'content' is missing: send the full file text (an empty string is a valid body). Nothing was written.",
            };
        }
        // Any other kind is echoed as text, exactly like the host's `GetString`. A JSON object or
        // array keeps its JSON FORM: `String()` would write `[object Object]` into the file here
        // while the VS host writes the raw JSON, so one approved call would produce two different
        // files. A boolean still differs in capitalisation (`true` here, `True` there).
        const raw = params.content;
        const content: string = typeof raw === 'string'
            ? raw
            : raw !== null && typeof raw === 'object'
                ? JSON.stringify(raw)
                : String(raw);
        const absPath = getAbsolutePath(filePath, workspaceRoot);

        // Encoding and line endings belong to the tree (.editorconfig) or to the existing file;
        // mode decides whether the content replaces the file, extends it or is prepended to it.
        return writeFileWithMode(filePath, absPath, content, parseMode(params.mode));
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 3. edit_file
export function editFiles(params: any, workspaceRoot: string): ToolResult {
    try {
        const filePath: string = params.filePath;

        // The SHAPE of `edits` is checked here with the same sentences the C# host builds in
        // `ToolCore/EditPayload.cs`, and for the same reason: a refusal has to name the edit, the
        // field and the value the model sent. The C# host used to pass a System.Text.Json exception
        // through, whose "LineNumber 5 | BytePositionInLine 20" pointed into a pretty-printed COPY
        // of a payload the model had sent as one line — so the model retried with another guess.
        //
        // The ORDER is part of the message, not an implementation detail: one pass per edit, in
        // DiffEdit's order (hint -> oldStr -> newStr -> replaceAll), and the shape of the WHOLE array
        // settled before the file is even resolved. The C# host does exactly this, so a payload with
        // two mistakes is answered with the SAME one by both hosts. Checking `existsSync` first, as
        // this function used to, answered "File doesn't exist." to a call the sibling host refused
        // for its shape.
        const edits: any = params.edits;

        if (edits === undefined || edits === null) {
            return {
                success: false,
                error: "'edits' is missing: send a JSON array of edits, " +
                    'e.g. [{"oldStr": "line to find", "newStr": "replacement"}]. Nothing was changed.',
            };
        }

        if (!Array.isArray(edits)) {
            return {
                success: false,
                error: `'edits' must be a JSON array of edit objects, received ${describeValue(edits)}. Nothing was changed.`,
            };
        }

        // Labels match `EditBatch.EditLabel`: "edit 2 of 3 (line 42)", and the hint is printed ONLY
        // when the model sent a usable one — a null hint used to show up as "line -1", a fractional
        // one as "line 1.5", and an out-of-int32 one as "line 3000000000". The C# host prints no hint
        // whenever the hint itself is what failed, so the range check belongs here too.
        const isUsableHint = (hint: any): boolean =>
            typeof hint === 'number' && Number.isInteger(hint) && hint >= INT32_MIN && hint <= INT32_MAX;

        const labelFor = (hint: any, index: number): string => {
            const label = `edit ${index + 1} of ${edits.length}`;
            return isUsableHint(hint) ? `${label} (line ${hint})` : label;
        };

        const canonical: any[] = [];

        for (let i = 0; i < edits.length; i++) {
            const edit = edits[i];

            // A JSON null INSIDE the array is legal and is NOT a shape error: the batch reports it
            // per edit ("the edit is null") so the siblings still apply. Refusing the whole call
            // here would disagree with the C# host, where EditPayload keeps a null element too.
            if (edit === null) {
                canonical.push(null);
                continue;
            }

            if (typeof edit !== 'object' || Array.isArray(edit)) {
                return {
                    success: false,
                    error: `edit ${i + 1} of ${edits.length}: must be a JSON object with 'oldStr' and 'newStr', ` +
                        `received ${describeValue(edit)}. Nothing was changed.`,
                };
            }

            const hint = readProperty(edit, 'approximateLine');
            // int32 only: the C# host stores the hint in an `int`, so a number outside that range is
            // a line the host cannot represent. `Number.isInteger` alone accepted 3000000000 and the
            // edit ran here while the sibling host refused it.
            if (hint !== undefined && hint !== null && !isUsableHint(hint)) {
                return {
                    success: false,
                    error: `${labelFor(hint, i)}: 'approximateLine' must be an integer or null, ` +
                        `received ${describeValue(hint)}. Nothing was changed.`,
                };
            }

            const label = labelFor(hint, i);

            // A number or an object in a text field used to reach `splitEditText` through a
            // substituted default and apply something the model never wrote.
            for (const field of ['oldStr', 'newStr']) {
                const value = readProperty(edit, field);
                if (value !== undefined && value !== null && typeof value !== 'string') {
                    return {
                        success: false,
                        error: `${label}: '${field}' must be a string, ` +
                            `received ${describeValue(value)}. Nothing was changed.`,
                    };
                }
            }

            // `replaceAll` is BOOLEAN or null, and a violation refuses the WHOLE batch — never the
            // one edit. Reading it tolerantly here (asBool) turned the same call into a file-wide
            // rewrite in this host only, and the two hosts then disagreed in the DESTRUCTIVE
            // direction.
            const raw = readProperty(edit, 'replaceAll');
            if (raw !== undefined && raw !== null && typeof raw !== 'boolean') {
                return {
                    success: false,
                    error: `${label}: 'replaceAll' must be true, false or null, ` +
                        `received ${describeValue(raw)}. Nothing was changed.`,
                };
            }

            canonical.push({
                approximateLine: typeof hint === 'number' ? hint : undefined,
                oldStr: readProperty(edit, 'oldStr'),
                newStr: readProperty(edit, 'newStr'),
                replaceAll: raw,
            });
        }

        // An empty batch is a legal payload the caller refuses in its own words — and it is refused
        // only once the SHAPE above is sound, exactly like the C# host.
        if (edits.length === 0) {
            return { success: false, error: "'edits' is empty: nothing to apply." };
        }

        const absPath = getAbsolutePath(filePath, workspaceRoot);

        if (!fs.existsSync(absPath)) {
            return { success: false, error: "File doesn't exist." };
        }

        // Encoding, separator and the final line break belong to the FILE, and the writer puts
        // back exactly what the reader found. A file that cannot be round-tripped (binary,
        // oversized, not UTF-8, no BOM, or bytes its own charset cannot reproduce) is refused here:
        // matching a garbled line list first would report a misleading "oldStr not found" and
        // destroy the file on the next successful edit.
        const file = readFileWithMetadata(absPath);
        if (file.status !== 'ok') {
            return { success: false, error: `edit NOT attempted on ${filePath}: ${file.skipReason}` };
        }

        const lines = file.lines;

        // Sort edits by approximateLine DESCENDING (no hint treated as -1).
        // Original indices are preserved: the model re-sends only failed edits
        // by their position in ITS request, not in the sorted copy.
        //
        // The comparator sees the CANONICAL view, where a hint is either a validated integer or
        // `undefined` — a non-numeric one was refused above. That is why there is no `NaN` guard
        // here any more: an inconsistent comparator would make `Array.prototype.sort` leave the
        // order implementation-defined, and edits would then run out of order against shifting
        // line numbers.
        const hintForSort = (edit: any): number =>
            typeof edit?.approximateLine === 'number' ? edit.approximateLine : -1;

        const sortedEdits = canonical
            .map((edit, index) => ({ edit, index }))
            .sort((a, b) => hintForSort(b.edit) - hintForSort(a.edit));

        let totalReplacements = 0;
        let appliedEdits = 0;
        const failures: string[] = [];

        for (const { edit, index } of sortedEdits) {
            // 1-based position in the MODEL's request (not in the sorted copy): `edit 2 of 3` is
            // what the model has to look at, and an off-by-one here points it at the wrong edit.
            const label = `edit ${index + 1} of ${edits.length}`;

            // A JSON `null` in the array is legal: refused per edit rather than thrown before the
            // loop, so the model gets a sentence it can act on instead of a TypeError message.
            if (edit === null) {
                failures.push(`${label}: the edit is null. Each edit needs 'oldStr' and 'newStr' strings.`);
                continue;
            }

            const hasHint = typeof edit.approximateLine === 'number';
            const hint = hasHint ? edit.approximateLine : -1;
            // ONE label shape for every failure. A missing oldStr/newStr used to report the bare
            // `edit 2 of 3` while not-found/ambiguous printed `edit 2 of 3 (line 42)`, so the same
            // edit arrived with a different name depending on how it failed. The C# twin
            // (`EditBatch.EditLabel`) always includes the line: keep both hosts identical — and the
            // line stays absent ONLY when the model sent no hint, never as `line -1`.
            const where = hasHint ? `${label} (line ${hint})` : label;

            // A null or OMITTED oldStr is refused BEFORE any matching: splitEditText(null) returns
            // [''], and an all-empty search legitimately matches the FIRST empty line of the file —
            // the edit would "succeed" by inserting newStr at a random blank line. An explicitly
            // empty oldStr stays legal: that is the legacy "matches an empty file line" semantics.
            // (The canonical view normalises "absent" to `undefined` and "null" stays null; both are
            // refusals, and the C# host reaches the same sentence through EditBatch.)
            if (edit.oldStr === null || edit.oldStr === undefined) {
                failures.push(`${where}: oldStr is missing`);
                continue;
            }

            // A null newStr cannot be told apart from "the model forgot the field": writing it as
            // an empty replacement would silently delete the matched line(s).
            if (edit.newStr === null || edit.newStr === undefined) {
                failures.push(`${where}: newStr is missing. Send the full replacement text; an empty string leaves an empty line, and a lone line cannot be deleted here (use create_file to rewrite the file).`);
                continue;
            }

            // splitEditText — the single normalisation point (mirrors EditApplier.SplitEditText):
            // mixed CRLF/LF/CR endings and one trailing line break treated as a terminator,
            // so an oldStr ending with \n does not turn into a phantom empty search line.
            const search = splitEditText(edit.oldStr);
            const replace = splitEditText(edit.newStr);

            // applyEdit is the ported EditApplier.ApplyEdit: it matches (fragment/boundary rules
            // included), selects (ambiguity refusal included) and REPLACES in one step, so the
            // matcher and the replacement cannot disagree about which part of a line the edit
            // covers — a mismatch there silently deletes whole lines.
            const outcome = applyEdit(lines, search, replace, hint, 5, edit.replaceAll === true);

            if (outcome.outcome === 'notFound') {
                failures.push(`${where}: oldStr not found`);
                continue;
            }

            if (outcome.outcome === 'ambiguous') {
                // Refuse instead of guessing: silently replacing the FIRST occurrence is how an
                // edit lands in the wrong place. Mirror of EditApplier's ambiguity rule.
                failures.push(`${where}: oldStr found ${outcome.matchLines.length} times ` +
                    `(${describeLines(outcome.matchLines)}) — add more context or set replaceAll: true`);
                continue;
            }

            appliedEdits++;
            totalReplacements += outcome.appliedCount;
        }

        // Per-edit report: the model sees WHICH edit failed and why — it can
        // re-send only the failed ones instead of the whole batch.
        const report = failures.length === 0
            ? ''
            : '\nFailed edits:\n - ' + failures.join('\n - ');

        // Dry run: edits are applied to in-memory lines (subsequent searches see
        // shifted lines) but the file is NOT written — checks that all oldStr
        // fragments are findable.
        if (asBool(params.dryRun)) {
            return {
                success: true,
                payload: `Dry run on ${filePath}: ${appliedEdits}/${edits.length} edits would apply, ` +
                    `${totalReplacements} replacements.${report}`
            };
        }

        if (appliedEdits === 0) {
            // Lead with the FILE: a batch may span several files and the model must know which one
            // is still untouched.
            return { success: false, error: `No valid replacements in ${filePath}.` + report };
        }

        // Back with the file's OWN separator, final line break, BOM and encoding. The write can
        // REFUSE — the text now contains whatever the model generated, and a character the file's
        // charset cannot hold must not reach disk as '?' (latin1) or U+FFFD (UTF-8/UTF-16) behind a
        // "changes successfully applied". On a refusal nothing was written: the report says why and
        // the file is still the readable original.
        const written = writeFileWithMetadata(absPath, lines, file, filePath);
        if (!written.ok) {
            return { success: false, error: written.message };
        }

        // A normalised file is REPORTED, never silently reformatted: the model may be looking at a
        // diff that shows changes it did not ask for. The sentence is `EditBatch.FormatReport`'s,
        // verbatim: it starts with its OWN line break (the failure list above ends without one, so a
        // leading space glued it into the last bullet) and it does NOT localise the rewrite —
        // `writeFileWithMetadata` re-joins EVERY line, so untouched lines change ending too.
        const note = file.hasMixedLineEndings
            ? `\nNote: the file had mixed line endings; it was rewritten with '${separatorName(file.separator)}' throughout.`
            : '';

        // Order matches the VS host (`{report}{mixedEndingsNote}`): the per-edit failure list comes
        // first, the normalisation note last, so the two hosts' payloads are comparable line by line.
        return {
            success: true,
            payload: `Changes successfully applied to ${filePath}. ` +
                `${appliedEdits}/${edits.length} edits, ${totalReplacements} replacements.${report}${note}`
        };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

/** Short list of the found line numbers (1-based) for an ambiguity report. */
function describeLines(matchLines: number[]): string {
    const shown = matchLines.slice(0, 5).map(line => line + 1).join(', ');
    return matchLines.length > 5 ? `lines ${shown}, …` : `lines ${shown}`;
}

// 4. delete_file
export function deleteFile(params: any, workspaceRoot: string): ToolResult {
    try {
        const p: string = params.path;
        const absPath = getAbsolutePath(p, workspaceRoot);
        if (fs.existsSync(absPath)) {
            fs.unlinkSync(absPath);
            return { success: true, payload: `File ${p} deleted successfully.` };
        } else {
            return { success: false, error: `File ${p} does not exist.` };
        }
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 5. dir
export function dir(params: any, workspaceRoot: string): ToolResult {
    try {
        const p: string = params.path || '.';
        const recursive: boolean = params.recursive ?? false;
        const absPath = getAbsolutePath(p, workspaceRoot);

        if (!fs.existsSync(absPath) || !fs.statSync(absPath).isDirectory()) {
            return { success: false, error: `Directory ${p} doesn't exist` };
        }

        const items = listDir(absPath, recursive);
        const relativeItems = items.map(full => makeRelative(full, workspaceRoot));
        return { success: true, payload: relativeItems.join('\n') };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

/**
 * Reads a boolean argument with the SAME tolerance as `ToolCore/JsonUtils.GetBool` in the VS host
 * and `UIBlazor/Utils/JsonUtils.GetBool` in the badge. `true`/`1`/`"true"`/`"1"`/`"yes"` are true,
 * `false`/`0`/`"false"`/`"0"`/`"no"` are false, anything else (including absent) is the default.
 *
 * NO TRIMMING, on purpose: the two C# readers compare the value's TEXT without stripping whitespace,
 * so a padded `" yes "` is false for them. Trimming here made this host the outlier in the dangerous
 * direction — `"dryRun": " yes "` skipped the write here while the VS host performed it, and the
 * badge (which also does not trim) showed no dry-run warning at all.
 *
 * One irreducible difference: a non-canonical spelling of one (`1.0`) is compared by the C# readers as
 * the raw text `"1.0"` (false) and arrives here already normalised to the number `1` (true). JSON
 * parsers in JS do not keep the source text, so this cannot be closed from this side.
 *
 * The tool schema is strict, so a string `"yes"` is formally a violation — but the VS host ACCEPTS it
 * and performs the call, and the UI draws the `dry run / nothing was written` badge from a tolerant
 * read. A strict `=== true` here therefore behaved differently from both of them in the worst
 * direction: the user approved a call on the strength of a badge saying the file was untouched, and
 * the file was written. Tolerance is agreement about what the call means, not laxity.
 */
export function asBool(value: unknown, defaultValue = false): boolean {
    if (typeof value === 'boolean') return value;
    if (typeof value === 'number') {
        if (value === 1) return true;
        if (value === 0) return false;
        return defaultValue;
    }
    if (typeof value === 'string') {
        const text = value.toLowerCase();
        if (text === 'true' || text === '1' || text === 'yes') return true;
        if (text === 'false' || text === '0' || text === 'no') return false;
    }
    return defaultValue;
}
