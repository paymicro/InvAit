/**
 * `create_file` writer: encoding and line endings per `.editorconfig`, plus append/prepend modes.
 *
 * Mirror of `ToolCore/FileWriter.cs` (the hosts share no code). The rules are decisions, not
 * accidents — keep them identical in both hosts:
 *
 * - NEW file: encoding from `.editorconfig` `charset`, default UTF-8 WITHOUT BOM. The old code
 *   passed `'utf-8'` to `fs.writeFileSync`, which is correct for Node but ignored the tree: a
 *   `charset = utf-8-bom` tree got files without a BOM and vice versa.
 * - EXISTING file: its own BYTES are the source of truth and are never decoded and re-encoded.
 *   The BOM stays, a file without one does not gain one, the separator is sniffed from the file's
 *   own bytes, and only the text THIS CALL adds is encoded. `overwrite` is the one mode that
 *   replaces the body, and even then it keeps the file's own preamble.
 * - A non-UTF-8 file WITHOUT a BOM cannot be read without a declared charset. `.editorconfig`
 *   `charset` supplies one; with no usable declaration the write is REFUSED instead of lossily
 *   re-encoding the file into U+FFFD. That refusal is the point of this module: before it, a
 *   cp1251 file with no BOM came back full of replacement characters while the report claimed
 *   "existing bytes preserved".
 * - A separator/encoding is only ENFORCED when it is known: a new file with no `.editorconfig`
 *   value keeps the content verbatim instead of guessing the platform newline.
 * - Only the text this call writes is normalised. If `.editorconfig` disagrees with what an
 *   existing file actually has, that is REPORTED in the result, never silently reformatted.
 *
 * WHAT IS NOT HERE ANY MORE: the BOM ladder, the strict-UTF-8 probe, the strict encoder, the
 * byte-splicing helpers and the separator algorithm all live in `encoding.ts`, because
 * `fileReader.ts` needs exactly the same primitives and two copies of an encoding decision drift
 * invisibly (they had: the reader could not see `latin1`, it refused NUL bytes this writer
 * accepted, and the two separator sniffers disagreed). What stays here is the FILE POLICY: which
 * charset a file is in, which refusal to issue, and how the new bytes are spliced onto the old.
 *
 * SYNCHRONOUS BY CONTRACT: the only caller (`fileOps.createFile`, owned by another change) returns
 * this result straight back as a `ToolResult`, so an async writer would force an edit there. The
 * fs calls stay synchronous; on a call that already reads the file synchronously that costs
 * nothing measurable.
 */

import * as fs from 'fs';
import * as path from 'path';
import { resolveSeparator, resolveEditorConfig, EditorConfigSettings } from './editorConfig';
import {
    CHARSETS,
    CharsetName,
    CharsetSpec,
    EMPTY_BUFFER,
    encodeStrict,
    endsWithLineBreak,
    isCreatableCharset,
    isValidUtf8,
    majoritySeparator,
    separatorName,
    sniffPreamble,
} from './encoding';

export type FileWriteMode = 'overwrite' | 'append' | 'prepend';

/**
 * How much of an existing file an OVERWRITE sniffs: the preamble and the first line breaks are
 * always inside it, and it keeps a 3 MB file from being read just to be replaced. Append/prepend
 * splice untouched bytes into the whole file, so they still read all of it.
 *
 * The VALUE and its reason mirror `FileWriter._sniffBytes` in `ToolCore/FileWriter.cs` (64 KB, the
 * window that is large enough for every first-line decision and small enough to be free on a big
 * file). The two hosts must decide the charset AND the separator from the same bytes: with the
 * whole file in view here and 64 KB there, a file above the window whose head is LF-majority and
 * whose tail is CRLF-heavy was written with a different newline — and a non-UTF-8 byte past the
 * window made this host refuse a write the other host performed. Same bytes, same answer.
 */
const SNIFF_BYTES = 64 * 1024;

export interface FileWriteResult {
    success: boolean;
    payload: string;
    /** Set only on failure — the same text as `payload`, mirroring `FileWriteResult.Fail` in C#. */
    error?: string;
}

/** Parses the tool's `mode` argument; null/empty means overwrite. */
export function parseMode(mode: unknown): FileWriteMode {
    const value = String(mode ?? '').trim().toLowerCase();
    if (value === '' || value === 'overwrite' || value === 'create' || value === 'replace') return 'overwrite';
    if (value === 'append') return 'append';
    if (value === 'prepend') return 'prepend';
    throw new Error(`Unknown mode '${String(mode)}'. Use overwrite, append or prepend.`);
}

// ---------------------------------------------------------------------------
// Detection
// ---------------------------------------------------------------------------

interface Detected {
    spec: CharsetSpec;
    /** Where the charset came from — the report must not claim the file "declares" a tree value. */
    source: 'bom' | 'probe' | 'editorconfig';
    /** Body bytes after the BOM: never decoded and never rewritten. */
    body: Buffer;
}

interface DetectionResult {
    detected?: Detected;
    refusal?: string;
}

/**
 * Classifies an existing file: BOM first (the ladder itself lives in `encoding.ts`, where the
 * UTF-32-before-UTF-16 order is owned), then a strict UTF-8 probe, then the declared
 * `.editorconfig` charset, then a refusal.
 *
 * `complete` says whether `bytes` is the WHOLE file or only its head: a partial window can end
 * inside a character, and the probe must not call that a non-UTF-8 file.
 */
function detectExisting(bytes: Buffer, settings: EditorConfigSettings, displayPath: string, complete: boolean): DetectionResult {
    const sniffed = sniffPreamble(bytes);
    if (sniffed !== undefined) {
        return {
            detected: { spec: sniffed.spec, source: 'bom', body: bytes.subarray(sniffed.bodyOffset) },
        };
    }

    if (isValidUtf8(bytes, complete)) {
        return { detected: { spec: CHARSETS['utf-8'], source: 'probe', body: bytes } };
    }

    const declared = settings.charset;
    if (declared !== undefined && isCreatableCharset(declared)) {
        // The tree names the charset the bytes are in. The body still stays byte-for-byte: only the
        // text this call adds is encoded.
        //
        // The PREAMBLE is dropped on purpose. We are in the branch where the file has NO BOM, and the
        // tree cannot add one to bytes it is not rewriting: a declared `utf-8-bom`/`utf-16le` used to
        // make overwrite and prepend EMIT that BOM — a prefix the C# host never writes, because there
        // it comes from the file's own identity (FileIdentity: "the file's own prefix must survive
        // unchanged either way"). Same approved call, two different files on disk. Append could never
        // add it (it concatenates the raw bytes), so the same charset also disagreed with itself.
        const spec = { ...CHARSETS[declared], preamble: EMPTY_BUFFER };
        return { detected: { spec, source: 'editorconfig', body: bytes } };
    }

    const reason = declared === undefined
        ? '.editorconfig declares no charset'
        : `.editorconfig declares charset '${declared}', which is not supported`;
    return {
        refusal: `File ${displayPath} is not valid UTF-8 and has no BOM, and ${reason}. ` +
            'Refusing to rewrite it — non-ASCII bytes would be lost. ' +
            'Set charset in .editorconfig, or edit the file with bash.',
    };
}

/** Bytes of one separator in the file's charset: a UTF-16 file stores CRLF as `0D 00 0A 00`. */
function separatorBytes(charset: CharsetName, separator: string): Buffer {
    return CHARSETS[charset].encode(separator);
}

function normalizeEol(text: string, separator: string): string {
    return text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n').join(separator);
}

// ---------------------------------------------------------------------------
// Writing
// ---------------------------------------------------------------------------

function readExistingBytes(absPath: string): Buffer {
    const raw = fs.readFileSync(absPath);
    return Buffer.isBuffer(raw) ? raw : Buffer.from(String(raw), 'utf8');
}

/**
 * The first `limit` bytes of an existing file — the WINDOW an overwrite decides on.
 *
 * Read through a descriptor so a file above the window is not loaded to be replaced (`ReadHead` in
 * `ToolCore/FileWriter.cs`). An `fs` without descriptor reads — a stubbed tree in a test, an
 * exotic host — still has to answer the same question, so the fallback reads and keeps the prefix:
 * the DECISIONS below are over `sniff` either way, which is what has to match C#.
 */
function readHead(absPath: string, limit: number): Buffer {
    try {
        const descriptor = fs.openSync(absPath, 'r');
        try {
            const buffer = Buffer.alloc(limit);
            const read = fs.readSync(descriptor, buffer, 0, limit, 0);
            return buffer.subarray(0, read);
        } finally {
            fs.closeSync(descriptor);
        }
    } catch {
        return readExistingBytes(absPath).subarray(0, limit);
    }
}

/**
 * Writes `content` to `absPath` honouring the tree and the existing file.
 * `displayPath` is what the user/model sees in the result (the path as requested).
 */
export function writeFileWithMode(displayPath: string, absPath: string, content: string, mode: FileWriteMode): FileWriteResult {
    const exists = fs.existsSync(absPath);

    // A leading U+FEFF in the CONTENT is a BOM in search of a home: the file's BOM belongs to the
    // resolved encoding, so a U+FEFF arriving as TEXT is always a SECOND one — at the very start of
    // an overwritten body, or buried mid-file by append/prepend, where nothing recognises it any
    // more (and in C# a stray U+FEFF before the first token is a compile error).
    //
    // Unconditional, and BEFORE the empty guard below: a body that was nothing but a U+FEFF is an
    // empty body, so a guard reading the unstripped text would still write the glue and grow the
    // file. The old guard fired only when the charset carried no BOM, so `charset = utf-8-bom` came
    // out with two BOMs in a row — the C# writer has stripped unconditionally all along.
    let text = content;
    let note = '';
    // `while`, not one slice: `"\uFEFF\uFEFF"` would otherwise leave one at the head of the body —
    // the "two BOMs in a row" this branch exists to prevent.
    let strippedFeff = 0;
    while (text.startsWith('\uFEFF')) {
        text = text.slice(1);
        strippedFeff++;
    }
    const hadLeadingFeff = strippedFeff > 0;

    // An empty append/prepend has nothing to add: refusing to write is the only way to keep the
    // file's bytes (and its mtime) exactly as they were. `Nothing to append` covers both modes
    // because the caller's intent — "add this text to the file" — is the same for both.
    if (exists && mode !== 'overwrite' && text.length === 0) {
        return {
            success: true,
            payload: `Nothing to append to ${displayPath}: the content was empty and the file was not modified.`,
        };
    }

    const settings: EditorConfigSettings = resolveEditorConfig(absPath);

    let bytes: Buffer;
    let spec: CharsetSpec;
    let charsetFromTree = false;
    let separator: string;
    let separatorEnforced: boolean;

    if (exists) {
        // `overwrite` REPLACES the body, so only the head of the file can influence the decision —
        // the preamble and the first line breaks are always inside `SNIFF_BYTES`. The other two
        // modes splice untouched bytes into the file, so they need every byte of it.
        const wholeFile = mode !== 'overwrite';
        const fileBytes = wholeFile ? readExistingBytes(absPath) : readHead(absPath, SNIFF_BYTES);
        // Can a "valid UTF-8" verdict be trusted? Only when the probe saw every byte. The exact
        // expression of the C# writer: `all != null || sniff.Length < _sniffBytes` — a window of
        // exactly SNIFF_BYTES from a larger file is NOT proof that the tail ends a character.
        const complete = wholeFile || fileBytes.length < SNIFF_BYTES;
        const detection = detectExisting(fileBytes, settings, displayPath, complete);
        const detected = detection.detected;
        if (detected === undefined) {
            const refusal = detection.refusal ?? `File ${displayPath} cannot be written.`;
            return { success: false, payload: refusal, error: refusal };
        }

        spec = detected.spec;
        charsetFromTree = detected.source === 'editorconfig';

        if (hadLeadingFeff) {
            note = strippedFeff === 1
                ? " Note: a leading U+FEFF was removed from the content: the file's BOM comes from its encoding and must not be repeated."
                : ` Note: ${strippedFeff} leading U+FEFF characters were removed from the content: the file's BOM comes from its encoding and must not be repeated.`;
        }

        const configured = resolveSeparator(settings);
        // The file's OWN majority break, counted over its own bytes — the same call the reader makes,
        // so an append and an edit of the same file can never pick two different newlines. For an
        // overwrite those bytes are the sniff WINDOW, exactly like `DetectSeparator(sniff, …)` in
        // the C# writer: the answer has to come from the same bytes in both hosts, and a tail the
        // window does not cover is not part of the question.
        const detectedSeparator = majoritySeparator(detected.body, spec.charset);
        separator = detectedSeparator ?? configured ?? '\n';
        // Printed ONLY when `.editorconfig` enforces `end_of_line`: a separator sniffed from the
        // file is that file's own state, not a rule this write applied. Same one condition as the
        // C# writer, and the one docs/dev/file-writes.md states.
        separatorEnforced = configured !== undefined;

        const chunk = encodeStrict(normalizeEol(text, separator), spec.charset, displayPath);
        if (!chunk.ok) return { success: false, payload: chunk.message, error: chunk.message };

        if (mode === 'overwrite') {
            // The body is replaced, so the file's own preamble is re-emitted and nothing else of the
            // old bytes survives.
            bytes = Buffer.concat([spec.preamble, chunk.bytes]);
        } else if (mode === 'append') {
            const glue = detected.body.length > 0 && !endsWithLineBreak(detected.body, spec.charset)
                ? separatorBytes(spec.charset, separator)
                : EMPTY_BUFFER;
            bytes = Buffer.concat([fileBytes, glue, chunk.bytes]);
        } else {
            // Prepend needs the separator when the NEW text does not end with one, otherwise it
            // would run into the first existing line. The preamble stays FIRST: `chunk + fileBytes`
            // would bury a BOM in the middle of the file, where nothing recognises it any more.
            const glue = detected.body.length > 0 && !endsWithLineBreak(chunk.bytes, spec.charset)
                ? separatorBytes(spec.charset, separator)
                : EMPTY_BUFFER;
            bytes = Buffer.concat([spec.preamble, chunk.bytes, glue, detected.body]);
        }
    } else {
        const chosen = encodingForNewFile(settings.charset);
        spec = chosen.spec;
        charsetFromTree = chosen.fromTree;

        // Same strip as above, only the note is charset-aware: with a BOM-carrying charset the
        // caller was repeating the preamble the encoding already emits, without one it would have
        // been written as a raw EF BB BF prefix — the invisible-diff defect.
        if (hadLeadingFeff) {
            note = strippedFeff === 1
                ? (spec.preamble.length === 0
                    ? ` Note: a leading U+FEFF was removed because charset '${spec.charset}' means no BOM.`
                    : " Note: a leading U+FEFF was removed from the content: the file's BOM comes from its encoding and must not be repeated.")
                : ` Note: ${strippedFeff} leading U+FEFF characters were removed from the content: the file's BOM comes from its encoding and must not be repeated.`;
        }

        const configured = resolveSeparator(settings);
        // No .editorconfig value: do not guess a newline — write the content verbatim. There is
        // therefore no separator to DETECT here (a new file has no bytes to sniff), and an
        // unconfigured one is never reported: `separator` is only read below when
        // `separatorEnforced`, which is exactly when `.editorconfig` supplied it.
        separator = configured ?? '\n';
        separatorEnforced = configured !== undefined;

        const encoded = encodeStrict(configured === undefined ? text : normalizeEol(text, configured), spec.charset, displayPath);
        if (!encoded.ok) return { success: false, payload: encoded.message, error: encoded.message };

        bytes = Buffer.concat([spec.preamble, encoded.bytes]);
    }

    const directory = path.dirname(absPath);
    if (directory) fs.mkdirSync(directory, { recursive: true });
    fs.writeFileSync(absPath, bytes);

    const action = !exists ? 'created' : mode === 'append' ? 'appended to' : mode === 'prepend' ? 'prepended to' : 'overwritten';
    const origin = charsetFromTree ? ' (from .editorconfig)' : '';
    const required = resolveSeparator(settings);

    let report = `File ${displayPath} ${action} successfully. Encoding: ${spec.charset}${origin}.`;
    // `=== separator` as well: the field states the rule THIS write enforced, so a declared
    // `end_of_line` the file does not use must not be printed as if it had been applied — the
    // mismatch note below is what says so, and printing both made the payload contradict itself.
    if (separatorEnforced && required === separator) report += ` Line endings: ${separatorName(separator)}.`;

    // What the caller must be TOLD instead of silently "fixed": the tree asks for one thing and the
    // file has another. Silence otherwise — a note on every append is noise the model learns to
    // ignore, and it made the two hosts report the same write differently.
    if (exists && !charsetFromTree && settings.charset !== undefined && settings.charset !== spec.charset) {
        report += ` Note: .editorconfig requires charset '${settings.charset}' but the file is '${spec.charset}'.`;
        report += onlyNewTextUsed(spec.charset, mode);
    } else if (!exists && settings.charset !== undefined && settings.charset !== spec.charset) {
        report += ` Note: charset '${settings.charset}' is not supported, wrote '${spec.charset}' instead.`;
    }

    if (exists && required !== undefined && required !== separator) {
        report += ` Note: .editorconfig requires end_of_line '${settings.endOfLine}' ` +
            `but the file uses '${separatorName(separator)}'.`;
        report += onlyNewTextUsed(separatorName(separator), mode);
    }

    report += note;

    return { success: true, payload: report };
}

/**
 * Tail of a mismatch note. Nothing in this module re-encodes untouched bytes, so the only truthful
 * thing left to say is WHICH part of the file the setting was applied to. Emitted only as the tail
 * of a note — mirror of `FileWriter.OnlyNewTextUsed` in ToolCore.
 */
function onlyNewTextUsed(used: string, mode: FileWriteMode): string {
    return mode === 'overwrite'
        ? ` The written content used '${used}'.`
        : ` The existing bytes were left untouched; only the new text used '${used}'.`;
}

/** Encoding a NEW file must use, from `.editorconfig` `charset`. */
function encodingForNewFile(charset: string | undefined): { spec: CharsetSpec; fromTree: boolean } {
    if (charset !== undefined && isCreatableCharset(charset)) {
        return { spec: CHARSETS[charset], fromTree: true };
    }
    // No value, or an unsupported one: UTF-8 WITHOUT BOM is the tree's default — never a hard-coded
    // BOM, which is what `File.WriteAllText(..., Encoding.UTF8)` used to produce.
    return { spec: CHARSETS['utf-8'], fromTree: false };
}
