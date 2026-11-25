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

// 1. read_files
export function readFiles(params: any, workspaceRoot: string): ToolResult {
    const files: any[] = params.files || [];
    const results: any[] = [];

    // Deduplicate by the WHOLE request, not by path alone: two ranges of one file are two
    // different reads, and the old key silently dropped the second one.
    const seen = new Set<string>();
    const deduped: any[] = [];
    for (const f of files) {
        const key = `${(f.path || '').toLowerCase()}|${f.startLine ?? -1}|${f.lineCount ?? -1}`;
        if (!seen.has(key)) {
            seen.add(key);
            deduped.push(f);
        }
    }

    for (const f of deduped) {
        const p: string = f.path;
        const startLine: number = f.startLine ?? -1;
        const lineCount: number = f.lineCount ?? -1;

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
            const needsLimit = lineCount <= 0;
            const takeCount = lineCount > 0 ? lineCount : (needsLimit ? 2000 : Infinity);
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
        const edits: any[] = params.edits || [];
        const absPath = getAbsolutePath(filePath, workspaceRoot);

        if (!fs.existsSync(absPath)) {
            return { success: false, error: "File doesn't exist." };
        }

        if (edits.length === 0) {
            return { success: false, error: "'edits' is empty: nothing to apply." };
        }

        // `replaceAll` is BOOLEAN or absent, and a violation refuses the WHOLE batch — never the one
        // edit. The C# host deserialises `edits` into List<DiffEdit> where ReplaceAll is `bool?`, so
        // System.Text.Json throws on "yes" / "true" / 1 and GetObject answers null, which
        // ToolExecutor reports as "Failed to read 'edits'" with nothing written. Reading it
        // tolerantly here (asBool) turned the same call into a file-wide rewrite in this host only,
        // and the two hosts then disagreed in the DESTRUCTIVE direction. The tolerance was added for
        // `dryRun`, which the C# host reads through JsonUtils.GetBool — but `replaceAll` reaches it
        // through DESERIALISATION, and that path is strict.
        for (let i = 0; i < edits.length; i++) {
            const edit = edits[i];
            const raw = edit?.replaceAll;
            if (raw !== undefined && raw !== null && typeof raw !== 'boolean') {
                return {
                    success: false,
                    error: `edit ${i + 1} of ${edits.length}: replaceAll must be true or false, not ${JSON.stringify(raw)}. Nothing was written.`,
                };
            }
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

        // Sort edits by approximateLine DESCENDING (null treated as -1).
        // Original indices are preserved: the model re-sends only failed edits
        // by their position in ITS request, not in the sorted copy.
        // A NON-NUMERIC hint is treated as "no hint" (-1), not as NaN: `NaN` makes the comparator
        // inconsistent, and `Array.prototype.sort` then leaves the order implementation-defined —
        // edits would run out of order against shifting line numbers.
        const hintForSort = (edit: any): number =>
            typeof edit?.approximateLine === 'number' && Number.isFinite(edit.approximateLine)
                ? edit.approximateLine
                : -1;

        // A JSON `null` inside the array is legal and must become a readable per-edit failure below,
        // not a TypeError before the loop — see `hintForSort` and the loop's guard.
        const sortedEdits = edits
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
            if (edit === null || typeof edit !== 'object') {
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

            // A null oldStr is refused BEFORE any matching: splitEditText(null) returns [''], and
            // an all-empty search legitimately matches the FIRST empty line of the file — the edit
            // would "succeed" by inserting newStr at a random blank line. An explicitly empty
            // oldStr stays legal: that is the legacy "matches an empty file line" semantics.
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
