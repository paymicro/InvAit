/**
 * Reading and writing a file with the metadata an edit round-trip MUST preserve.
 *
 * `edit_file` reads a file, changes some lines and writes it back. Everything the generic
 * `readFileSync(path, 'utf-8')` + `join('\n')` + `writeFileSync(path, text, 'utf-8')` pair threw
 * away is carried through here instead:
 *
 * - ENCODING and BOM: a UTF-16 or BOM-ed UTF-8 file is decoded with its own encoding and written
 *   back with the same bytes. A file that is neither BOM-tagged nor valid UTF-8 is REFUSED —
 *   silently decoding it as latin1 would rewrite it as garbage on the very next edit.
 * - LINE SEPARATOR: the majority of CRLF / LF / CR, so a CR-only file stays CR-only. The default
 *   for a file without any line break is '\n', never the platform's `os.EOL`: an edit must not
 *   depend on the machine that ran it.
 * - FINAL LINE BREAK: a file without one does not gain one, a file with one keeps it.
 * - DECODE FIDELITY: a decode that is not the exact inverse of the encode is a REFUSAL, not a
 *   warning. Node never fails on bad bytes — `toString('utf8')` substitutes U+FFFD, `utf16le`
 *   silently drops an odd trailing byte, UTF-32 clamps an out-of-range unit — and the write path
 *   would then put those replacements back into the file while reporting success. The read
 *   re-encodes what it decoded and compares the bytes; a mismatch refuses the edit before
 *   anything is matched, exactly where the C# reader refuses in `DecoderFallbackException`.
 * - WRITE: the text an edit produced is encoded with `encodeStrict`, so a character the file's
 *   charset cannot represent refuses the whole write instead of reaching disk as `'?'` (`latin1`)
 *   or U+FFFD (UTF-8/UTF-16) — the same corruption, harder to see.
 * - Binary and oversized files are refused BEFORE matching (`status` + `skipReason`), so the model
 *   reads "edit NOT attempted on X: file looks binary" instead of a misleading "oldStr not found".
 *
 * This mirrors the intent of `ToolCore/FileUtils.cs` (`ReadFileWithMetadataAsync` /
 * `SaveFileAsync`), which is being extended with the same `Status` / `SkipReason` channel. The
 * hosts share no code: change the rules together.
 *
 * The BOM ladder, the strict-UTF-8 probe and the separator algorithm are NOT implemented here any
 * more — they live in `encoding.ts`, together with the writer's copies of the same primitives,
 * because two copies of an encoding decision drift silently. What stays here is the READER's
 * policy: what is too large, what looks binary, which refusal to report and how a line list plus
 * the final-newline flag are re-joined.
 *
 * BOM sniffing order matters: `FF FE 00 00` is UTF-32LE, and testing UTF-16LE first would misread
 * a UTF-32LE file as UTF-16LE with a NUL in the second byte. `encoding.ts` owns that order.
 */

import * as fs from 'fs';
import {
    CHARSETS,
    CharsetName,
    CharsetSpec,
    encodeStrict,
    isCreatableCharset,
    isValidUtf8,
    majoritySeparator,
    sniffPreamble,
} from './encoding';
import { resolveEditorConfig } from './editorConfig';

export { separatorName } from './encoding';

/** Files above this are refused: edit_file rewrites the whole file, twice. Not exported: nobody outside reads it. */
const MAX_EDIT_FILE_BYTES = 2 * 1024 * 1024;

/** How far into the file a NUL byte is looked for (the cheapest binary signal there is). */
const BINARY_PROBE_BYTES = 1024;

/**
 * Why a read did or did not produce lines. `'binary'` is the shared word for "nothing was decoded"
 * and covers the three ways that happens: a NUL byte in the first 1 KB, a BOM-less file that is not
 * strict UTF-8 and has no declared charset, and bytes the charset named for it cannot reproduce —
 * the same three the C# `FileReadStatus.Binary` covers, with `skipReason` saying which one.
 */
export type FileReadStatus = 'ok' | 'notFound' | 'tooLarge' | 'binary';

/** The encoding of a file, as bytes plus the two functions needed to round-trip it. */
export interface FileEncoding {
    /**
     * The charset itself — reported to the model AND re-used to encode on write, so it is the
     * `CharsetName` the rest of the codec speaks, never a free-form string.
     */
    name: CharsetName;
    /** The exact BOM bytes to write back; empty when the file has none. */
    bom: Buffer;
    /** Decodes the bytes AFTER the BOM into text. */
    decode: (bytes: Buffer) => string;
    /** Encodes text into bytes (WITHOUT the BOM — the writer prepends it); byte-preserving, see `asFileEncoding`. */
    encode: (text: string) => Buffer;
}

export interface FileMetadata {
    /** 'ok' means `lines` is usable; every other status carries a `skipReason`. */
    status: FileReadStatus;
    /** The file's lines WITHOUT any line ending (empty unless status is 'ok'). */
    lines: string[];
    encoding: FileEncoding;
    /** The separator to re-join with: '\r\n', '\n' or '\r'. */
    separator: string;
    /** Whether the file ended with a line break (to be restored on write). */
    hasFinalNewLine: boolean;
    /** True when more than one kind of line break occurs in the file. */
    hasMixedLineEndings: boolean;
    /** Why the edit must not be attempted; undefined when status is 'ok'. */
    skipReason?: string;
}

/**
 * Outcome of writing the edit back: a refusal is a first-class result, not an exception and never a
 * silent substitution.
 */
export type FileWriteOutcome = { ok: true } | { ok: false; message: string };

/**
 * The READER's spelling of a charset: `encoding.ts` calls the pieces `charset` / `preamble`, the
 * metadata has always exposed them as `name` / `bom`. A rename, not a second policy — the
 * functions are the very same closures.
 *
 * `encode` here stays `CharsetSpec.encode`, the BYTE-PRESERVING one, for two reasons. It is what
 * proves the READ was lossless (`readFileWithMetadata` re-encodes the decoded text and compares),
 * and that check is meaningless with a substituting encoder — `encodeStrict` would refuse the
 * text instead of telling us whether the FILE is decodable. The text an edit WRITES does not come
 * through here any more: `writeFileWithMetadata` asks `encodeStrict` directly, because a model-
 * generated character the charset cannot hold must refuse the write rather than become `'?'`.
 */
function asFileEncoding(spec: CharsetSpec): FileEncoding {
    return { name: spec.charset, bom: spec.preamble, decode: spec.decode, encode: spec.encode };
}

/**
 * True when the text contains more than one KIND of line break. It deliberately does not say WHICH
 * one wins — that decision is made once, in `encoding.ts` (`majoritySeparator`) — it only tells the
 * caller that re-joining with the winning separator is a normalisation worth reporting.
 */
function hasMixedLineEndings(text: string): boolean {
    let seenCrlf = false;
    let seenLf = false;
    let seenCr = false;
    for (let i = 0; i < text.length; i++) {
        const code = text.charCodeAt(i);
        if (code === 0x0d) {
            if (text.charCodeAt(i + 1) === 0x0a) {
                seenCrlf = true;
                i++;
            } else {
                seenCr = true;
            }
        } else if (code === 0x0a) {
            seenLf = true;
        }
        if ((seenCrlf ? 1 : 0) + (seenLf ? 1 : 0) + (seenCr ? 1 : 0) > 1) return true;
    }
    return false;
}

function megabytesLabel(size: number): string {
    return (size / (1024 * 1024)).toFixed(1);
}

/** Byte size of the file, or undefined when the filesystem cannot tell (stubbed, exotic fs). */
function fileSize(absPath: string): number | undefined {
    try {
        const stats = fs.statSync(absPath) as unknown as { size?: number } | undefined;
        return typeof stats?.size === 'number' ? stats.size : undefined;
    } catch {
        return undefined;
    }
}

function emptyMetadata(): FileMetadata {
    return {
        status: 'ok',
        lines: [],
        encoding: asFileEncoding(CHARSETS['utf-8']),
        separator: '\n',
        hasFinalNewLine: false,
        hasMixedLineEndings: false,
    };
}

/**
 * How a byte buffer became text: the charset that produced it, the text itself, and the body bytes
 * the decode saw (callers need them for the BYTE-level separator count). A refusal names the charset
 * and why, in words the model and the edit path both read.
 */
export type TextDecodeOutcome =
    | { ok: true; text: string; spec: CharsetSpec; body: Buffer }
    | { ok: false; spec: CharsetSpec; reason: string };

/**
 * The ladder itself: BOM first, then the charset the CALLER resolved, then UTF-8 — and the result
 * VERIFIED by re-encoding it, because Node never fails on bad bytes.
 *
 * Why it is a function at all: `read_files` used to do `readFileSync(path, 'utf-8')` while `edit_file`
 * walked the full ladder, so a UTF-16 file reached the model as mojibake from one tool and
 * round-tripped untouched through the other. Two copies of an encoding decision do not drift loudly —
 * they drift silently, and the symptom lands in the model's context.
 *
 * It deliberately knows NOTHING about the file beyond its bytes: the NUL probe and the
 * `.editorconfig` fallback need a path and are the same for every caller, so they live one level up
 * in `decodeWorkspaceFileBytes`. This level stays a pure function over a buffer, which is what makes
 * it testable without stubs.
 */
export function decodeTextBytes(bytes: Buffer, declaredCharset?: CharsetName): TextDecodeOutcome {
    // The BOM ladder decides both the charset and where the body starts.
    const sniffed = sniffPreamble(bytes);
    const spec = sniffed !== undefined
        ? sniffed.spec
        : declaredCharset !== undefined
            ? CHARSETS[declaredCharset]
            : CHARSETS['utf-8'];
    const body = sniffed === undefined ? bytes : bytes.subarray(sniffed.bodyOffset);

    const text = spec.decode(body);

    // Node NEVER fails on bad bytes: `toString('utf8')` substitutes U+FFFD, `utf16le` drops an odd
    // trailing byte, UTF-32 clamps an out-of-range unit. Decoding is therefore not proof of
    // anything on its own — but a decode that its OWN encoder cannot undo is proof the file is not
    // in that charset (a lone 0xFF under a UTF-8 BOM, an odd-length UTF-16 body), and writing the
    // decoded text back would put those replacement bytes on disk. So the READ verifies itself:
    // re-encode with the very same byte-preserving function and compare. A mismatch is a REFUSAL,
    // never a warning.
    //
    // This is deliberately NOT a check on the model's new text: that text is not part of `body`,
    // and a character the charset cannot hold is refused on the WRITE side (`encodeStrict`), which
    // names the file and the character. Here the question is only "were these bytes decoded
    // losslessly", and the answer must be yes before a single line is matched or printed.
    if (!spec.encode(text).equals(body)) {
        return {
            ok: false,
            spec,
            // Tool-NEUTRAL: both callers print this, and a read does not write anything back, so a
            // sentence about "what an edit would write" was a description of a consequence that one
            // of them cannot have. What is true for both is that the text would invent characters.
            reason: `file is not valid ${spec.charset}: decoding it and re-encoding the result does not ` +
                'reproduce the same bytes (a replacement character, a dropped trailing byte or a clamped ' +
                'code point). The text would contain characters the file does not have.',
        };
    }

    return { ok: true, text, spec, body };
}

/**
 * The DECISION both tools share: what a workspace file's bytes mean, or why they cannot be shown as
 * text. `decodeTextBytes` is the ladder itself; this adds the two inputs a caller cannot invent on
 * its own — the NUL probe and the tree's `charset` as the last resort — so that `read_files` and
 * `edit_file` cannot answer differently about the SAME file.
 *
 * Splitting them that way is the whole point: the decision is shared, the CONSEQUENCES are not. The
 * edit path adds a 2 MB window and answers with `FileMetadata` (`status` / `skipReason`); the read
 * path adds neither a window nor a rewrite, and answers with one entry's `error`.
 */
export function decodeWorkspaceFileBytes(absPath: string, bytes: Buffer): TextDecodeOutcome {
    const sniffed = sniffPreamble(bytes);
    if (sniffed === undefined) {
        // No BOM: the file must describe itself as UTF-8, or the tree must name the charset it was
        // written in — otherwise decoding it at all is a guess. The probe is charset-free and runs
        // only for a BOM-less file: ASCII bytes inside UTF-16 contain NULs, so probing a BOM-ed file
        // first would declare every UTF-16/UTF-32 file binary.
        if (bytes.subarray(0, BINARY_PROBE_BYTES).includes(0)) {
            return {
                ok: false,
                spec: CHARSETS['utf-8'],
                reason: 'file looks binary (NUL byte in the first 1 KB)',
            };
        }
        if (!isValidUtf8(bytes)) {
            // `.editorconfig` may declare the charset. Refusing without asking made the same file
            // editable in Visual Studio and unreadable here — the divergence the shared codec was
            // brought in to end.
            const declared = resolveEditorConfig(absPath).charset;
            if (declared === undefined || !isCreatableCharset(declared)) {
                return {
                    ok: false,
                    spec: CHARSETS['utf-8'],
                    reason: declared === undefined
                        ? 'file has no BOM and is not valid UTF-8, and .editorconfig declares no charset. ' +
                          'Add charset to .editorconfig, or edit the file with bash.'
                        : `file has no BOM and is not valid UTF-8, and .editorconfig declares charset '${declared}', ` +
                          'which is not supported. Declare a supported charset, or edit the file with bash.',
                };
            }
            return decodeTextBytes(bytes, declared);
        }
    }

    return decodeTextBytes(bytes);
}

/**
 * Reads a file into lines plus everything needed to write it back unchanged.
 *
 * Order of the refusals: size (cheap, before loading a huge file) -> identity (`decodeWorkspaceFileBytes`)
 * -> content. The identity is the one both file tools share; the window above it is this path's own.
 */
export function readFileWithMetadata(absPath: string): FileMetadata {
    if (!fs.existsSync(absPath)) {
        return { ...emptyMetadata(), status: 'notFound', skipReason: 'file doesn\'t exist' };
    }

    const size = fileSize(absPath);
    if (size !== undefined && size > MAX_EDIT_FILE_BYTES) {
        return {
            ...emptyMetadata(),
            status: 'tooLarge',
            skipReason: `file is ${megabytesLabel(size)} MB, above the 2 MB edit limit`,
        };
    }

    const raw = fs.readFileSync(absPath);
    // Defensive: a stubbed/lying statSync must not let a huge buffer through.
    const bytes = Buffer.isBuffer(raw) ? raw : Buffer.from(String(raw), 'utf8');
    if (bytes.length > MAX_EDIT_FILE_BYTES) {
        return {
            ...emptyMetadata(),
            status: 'tooLarge',
            skipReason: `file is ${megabytesLabel(bytes.length)} MB, above the 2 MB edit limit`,
        };
    }

    // The identity of the file — BOM, probe, `.editorconfig` — is the SAME call `read_files` makes,
    // so the two tools cannot disagree about whether a file is readable text.
    const decoded = decodeWorkspaceFileBytes(absPath, bytes);
    if (!decoded.ok) {
        return {
            ...emptyMetadata(),
            encoding: asFileEncoding(decoded.spec),
            // 'binary' is the shared word for "nothing was decoded" — the same status the C# reader
            // reports for `DecoderFallbackException`, so the two hosts' reports stay comparable.
            status: 'binary',
            skipReason: decoded.reason,
        };
    }

    const { text, spec, body } = decoded;

    const hasFinalNewLine = text.length > 0 && (text.endsWith('\n') || text.endsWith('\r'));

    const lines = text.length === 0 ? [] : text.split(/\r\n|\r|\n/);
    // The trailing element produced by a final line break is a terminator, not an empty line —
    // `hasFinalNewLine` remembers it and the writer puts it back.
    if (hasFinalNewLine && lines.length > 0 && lines[lines.length - 1] === '') {
        lines.pop();
    }

    return {
        status: 'ok',
        lines,
        encoding: asFileEncoding(spec),
        // Same call the writer makes on the same bytes, so an edit and an append cannot disagree.
        separator: majoritySeparator(body, spec.charset) ?? '\n',
        hasFinalNewLine,
        hasMixedLineEndings: hasMixedLineEndings(text),
    };
}

/**
 * Writes `lines` back with the metadata's separator, final line break, BOM and encoding.
 * Nothing is normalised here: an edit that touched one line must not reformat the file.
 *
 * The encoder is `encodeStrict`, NOT the metadata's byte-preserving `encode`, because this text is
 * NEW: whatever the model wrote is in it, so a character the file's charset cannot represent is a
 * refusal with a message it can act on. Writing it anyway would put `'?'` (latin1) or U+FFFD
 * (UTF-8/UTF-16) in the file and still report "changes successfully applied" — for a character the
 * user asked for, that is silent data loss. `displayPath` is what the model saw, which is what the
 * refusal has to name.
 */
export function writeFileWithMetadata(
    absPath: string,
    lines: string[],
    metadata: FileMetadata,
    displayPath: string,
): FileWriteOutcome {
    let text = lines.join(metadata.separator);
    if (metadata.hasFinalNewLine) text += metadata.separator;

    const encoded = encodeStrict(text, metadata.encoding.name, displayPath);
    // Nothing is written on a refusal: a half-applied batch is worse than a refused one, and the
    // file on disk is still the readable original.
    if (!encoded.ok) return { ok: false, message: encoded.message };

    fs.writeFileSync(absPath, Buffer.concat([metadata.encoding.bom, encoded.bytes]));
    return { ok: true };
}
