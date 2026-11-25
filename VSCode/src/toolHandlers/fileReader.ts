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
 * Reads a file into lines plus everything needed to write it back unchanged.
 *
 * Order of the refusals: size (cheap, before loading a huge file) -> encoding (BOM first, then a
 * NUL probe and a strict UTF-8 probe for a BOM-less file) -> content.
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

    // The BOM ladder decides both the charset and where the body starts.
    const sniffed = sniffPreamble(bytes);
    let declaredCharset: CharsetName | undefined;
    if (sniffed === undefined) {
        // No BOM: the file must describe itself as UTF-8, or the tree must name the charset it was
        // written in — otherwise decoding it at all is a guess.
        if (bytes.subarray(0, BINARY_PROBE_BYTES).includes(0)) {
            return {
                ...emptyMetadata(),
                status: 'binary',
                skipReason: 'file looks binary (NUL byte in the first 1 KB)',
            };
        }
        if (!isValidUtf8(bytes)) {
            // `.editorconfig` may declare the charset. Refusing without asking made the same file
            // editable in Visual Studio and unreadable here — the divergence the shared codec was
            // brought in to end.
            const declared = resolveEditorConfig(absPath).charset;
            if (declared === undefined || !isCreatableCharset(declared)) {
                return {
                    ...emptyMetadata(),
                    status: 'binary',
                    skipReason: declared === undefined
                        ? 'file has no BOM and is not valid UTF-8, and .editorconfig declares no charset. ' +
                          'Add charset to .editorconfig, or edit the file with bash.'
                        : `file has no BOM and is not valid UTF-8, and .editorconfig declares charset '${declared}', ` +
                          'which is not supported. Declare a supported charset, or edit the file with bash.',
                };
            }
            declaredCharset = declared;
        }
    }

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
    // re-encode with the very same byte-preserving function and compare.
    //
    // This is deliberately NOT a check on the model's new text: that text is not part of `body`,
    // and a character the charset cannot hold is refused on the WRITE side (`encodeStrict`), which
    // names the file and the character. Here the question is only "were these bytes decoded
    // losslessly", and the answer must be yes before a single line is matched.
    if (!spec.encode(text).equals(body)) {
        return {
            ...emptyMetadata(),
            encoding: asFileEncoding(spec),
            // 'binary' is the shared word for "nothing was decoded" — the same status the C# reader
            // reports for `DecoderFallbackException`, so the two hosts' reports stay comparable.
            status: 'binary',
            skipReason: `file is not valid ${spec.charset}: decoding it and re-encoding the result does not ` +
                'reproduce the same bytes (a replacement character, a dropped trailing byte or a clamped ' +
                'code point would be written back by an edit)',
        };
    }

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
