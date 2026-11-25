/**
 * Encoding and line-separator primitives for the VS Code tool host — the ONE owner of both.
 *
 * WHY THIS MODULE EXISTS: `fileWriter.ts` and `fileReader.ts` each used to carry their own BOM
 * ladder, their own strict-UTF-8 probe, their own byte helpers and their own separator algorithm,
 * and the copies had already drifted: the reader could not see a `latin1` file the writer could
 * write, the reader refused NUL bytes the writer accepted, and the two separator sniffers answered
 * differently on the same file. Two copies of an ENCODING decision do not drift loudly — they drift
 * silently, and the symptom is a corrupted file. There is now exactly one copy of each decision and
 * both modules call into it.
 *
 * PURE BY CONTRACT: this file imports NOTHING — no `fs`, no `path`, no `.editorconfig` resolution.
 * That is what lets it be tested without stubs, and it keeps the FILE POLICY (which refusal to
 * issue, which charset a tree declares, which mode the caller asked for) in the modules that own
 * the filesystem. Mirror of `ToolCore/FileWriter.cs` + `ToolCore/FileUtils.cs` on the C# side,
 * which make the same decisions over the same bytes.
 *
 * TWO ENCODERS, DELIBERATELY — do NOT collapse them into one:
 *
 * - `CharsetSpec.encode` is BYTE-PRESERVING: it maps the string back the way the decoder produced
 *   it, so an unpaired surrogate in a UTF-16 file comes back as the very same code unit. That is
 *   what a round trip (`edit_file` reading a file and writing it back) requires: the file must not
 *   change because we opened it. It is also LOSSY for `latin1`, where a character outside Latin-1
 *   becomes a stray byte.
 * - `encodeStrict` VALIDATES FIRST and refuses instead of substituting: `Buffer.from(text, 'latin1')`
 *   writes `0x3F` for every out-of-range character and UTF-8/UTF-16 turn a lone surrogate into
 *   U+FFFD, and both are indistinguishable from corruption once on disk. Every path that CREATES
 *   text — `create_file`'s content, and the changed lines an `edit_file` writes back — uses this
 *   one; the path that RETURNS existing text (and the round-trip check that proves the return was
 *   lossless) uses the former. Two different questions, two different answers.
 *
 * BOM ORDER MATTERS: `FF FE 00 00` is UTF-32LE. Testing UTF-16LE first would read it as UTF-16LE
 * followed by a NUL and the next write would splice UTF-16 bytes into a UTF-32 file.
 */

export type CharsetName = 'utf-8' | 'utf-8-bom' | 'latin1' | 'utf-16le' | 'utf-16be' | 'utf-32le' | 'utf-32be';

/** A charset as bytes plus the two functions needed to round-trip through it. */
export interface CharsetSpec {
    charset: CharsetName;
    /** Bytes this charset puts in front of the body: the BOM, or nothing for a BOM-less charset. */
    preamble: Buffer;
    /** Decodes the body — the bytes AFTER the preamble — into text. */
    decode: (body: Buffer) => string;
    /** Encodes text WITHOUT the preamble, byte-preserving (see the header: this is not `encodeStrict`). */
    encode: (text: string) => Buffer;
}

/** Shared empty buffer: concatenating it is a no-op, and it cannot be written into. */
export const EMPTY_BUFFER: Buffer = Buffer.alloc(0);

const BOM_UTF8 = Buffer.from([0xEF, 0xBB, 0xBF]);
const BOM_UTF16LE = Buffer.from([0xFF, 0xFE]);
const BOM_UTF16BE = Buffer.from([0xFE, 0xFF]);
const BOM_UTF32LE = Buffer.from([0xFF, 0xFE, 0x00, 0x00]);
const BOM_UTF32BE = Buffer.from([0x00, 0x00, 0xFE, 0xFF]);

/** Node's Buffer has no UTF-16BE codec: swap the byte pairs of the UTF-16LE form. */
function swapBytePairs(buffer: Buffer): Buffer {
    const copy = Buffer.from(buffer);
    for (let i = 0; i + 1 < copy.length; i += 2) {
        const first = copy[i];
        copy[i] = copy[i + 1];
        copy[i + 1] = first;
    }
    return copy;
}

/** UTF-32 has no Node codec either: one 4-byte unit per code point, astral characters included. */
function encodeUtf32(text: string, bigEndian: boolean): Buffer {
    // Upper bound: a lone code unit never needs more than four bytes.
    const out = Buffer.alloc(text.length * 4);
    let offset = 0;
    for (const character of text) {
        const point = character.codePointAt(0) ?? 0;
        if (bigEndian) out.writeUInt32BE(point, offset);
        else out.writeUInt32LE(point, offset);
        offset += 4;
    }
    return out.subarray(0, offset);
}

/** Decodes UTF-32 by hand; a code point beyond U+10FFFF (impossible in valid input) becomes U+FFFD. */
function decodeUtf32(bytes: Buffer, bigEndian: boolean): string {
    let text = '';
    for (let i = 0; i + 3 < bytes.length; i += 4) {
        const codePoint = bigEndian ? bytes.readUInt32BE(i) : bytes.readUInt32LE(i);
        text += String.fromCodePoint(codePoint <= 0x10ffff ? codePoint : 0xfffd);
    }
    return text;
}

export const CHARSETS: Record<CharsetName, CharsetSpec> = {
    'utf-8': {
        charset: 'utf-8',
        preamble: EMPTY_BUFFER,
        decode: bytes => bytes.toString('utf8'),
        encode: text => Buffer.from(text, 'utf8'),
    },
    'utf-8-bom': {
        charset: 'utf-8-bom',
        preamble: BOM_UTF8,
        decode: bytes => bytes.toString('utf8'),
        encode: text => Buffer.from(text, 'utf8'),
    },
    latin1: {
        charset: 'latin1',
        preamble: EMPTY_BUFFER,
        decode: bytes => bytes.toString('latin1'),
        encode: text => Buffer.from(text, 'latin1'),
    },
    'utf-16le': {
        charset: 'utf-16le',
        preamble: BOM_UTF16LE,
        decode: bytes => bytes.toString('utf16le'),
        encode: text => Buffer.from(text, 'utf16le'),
    },
    'utf-16be': {
        charset: 'utf-16be',
        preamble: BOM_UTF16BE,
        decode: bytes => swapBytePairs(bytes).toString('utf16le'),
        encode: text => swapBytePairs(Buffer.from(text, 'utf16le')),
    },
    'utf-32le': {
        charset: 'utf-32le',
        preamble: BOM_UTF32LE,
        decode: bytes => decodeUtf32(bytes, false),
        encode: text => encodeUtf32(text, false),
    },
    'utf-32be': {
        charset: 'utf-32be',
        preamble: BOM_UTF32BE,
        decode: bytes => decodeUtf32(bytes, true),
        encode: text => encodeUtf32(text, true),
    },
};

/**
 * The charsets `.editorconfig` may ask for when CREATING a file — exactly the set
 * `EditorConfigSettings.CreateEncoding` accepts in C#. UTF-32 is detection-only: a file that
 * already carries a UTF-32 BOM keeps it, but the tree never asks us to start one.
 */
export const CREATABLE_CHARSETS: readonly CharsetName[] = ['utf-8', 'utf-8-bom', 'latin1', 'utf-16le', 'utf-16be'];

export function isCreatableCharset(value: string): value is CharsetName {
    return (CREATABLE_CHARSETS as readonly string[]).includes(value);
}

// ---------------------------------------------------------------------------
// Detection
// ---------------------------------------------------------------------------

/** Does `needle` occur at `offset` in `bytes`? Bounds-checked, so it never reads past the end. */
function matchesAt(bytes: Buffer, needle: Buffer, offset: number): boolean {
    if (offset + needle.length > bytes.length) return false;
    for (let i = 0; i < needle.length; i++) {
        if (bytes[offset + i] !== needle[i]) return false;
    }
    return true;
}

/**
 * The BOM ladder, in the only safe order: UTF-8 first (its prefix collides with nothing), then
 * UTF-32 BEFORE UTF-16 — `FF FE 00 00` is UTF-32LE, and testing UTF-16LE first would read it as
 * UTF-16LE with a NUL in the second byte.
 */
const PREAMBLES: readonly { bytes: Buffer; charset: CharsetName }[] = [
    { bytes: BOM_UTF8, charset: 'utf-8-bom' },
    { bytes: BOM_UTF32LE, charset: 'utf-32le' },
    { bytes: BOM_UTF32BE, charset: 'utf-32be' },
    { bytes: BOM_UTF16LE, charset: 'utf-16le' },
    { bytes: BOM_UTF16BE, charset: 'utf-16be' },
];

/** The charset a file's leading bytes declare, and where its body starts. Undefined when BOM-less. */
export function sniffPreamble(bytes: Buffer): { spec: CharsetSpec; bodyOffset: number } | undefined {
    for (const candidate of PREAMBLES) {
        if (matchesAt(bytes, candidate.bytes, 0)) {
            return { spec: CHARSETS[candidate.charset], bodyOffset: candidate.bytes.length };
        }
    }
    return undefined;
}

/**
 * Drops a VALID multi-byte sequence that the window edge cut in half.
 *
 * Only a truncation is tolerated: a lead byte that is not a UTF-8 lead at all (`0xF8`, a stray
 * continuation byte) means the file is genuinely not UTF-8, and hiding it here would let the
 * writer re-encode those bytes as EF BF BD. Hence the lead must be in `C2`–`F4` and the sequence
 * must need MORE bytes than the window holds.
 */
function trimIncompleteTail(bytes: Buffer): Buffer {
    // Four bytes is the longest sequence, so at most three of them can have started before the end.
    for (let back = 1; back <= 4 && back <= bytes.length; back++) {
        const index = bytes.length - back;
        const lead = bytes[index];
        if ((lead & 0xc0) === 0x80) continue; // a continuation byte: its lead is further back
        if (lead < 0xc2 || lead > 0xf4) return bytes; // not a UTF-8 lead: not a truncation
        const needed = lead >= 0xf0 ? 4 : lead >= 0xe0 ? 3 : 2;
        return needed > back ? bytes.subarray(0, index) : bytes;
    }
    return bytes;
}

/**
 * Strict UTF-8 validity — a PROBE, not a decode.
 *
 * `Buffer.toString('utf8')` substitutes U+FFFD and reports nothing, so it can never answer this
 * question; `TextDecoder` with `fatal: true` throws on the first byte sequence it cannot reproduce.
 * That is the only reason `TextDecoder` is used here at all.
 *
 * `complete` is the second half of `TextFileCodec.IsStrictUtf8(bytes, complete)` in C#. A caller
 * that probes a WINDOW of a file (`ToolCore/FileWriter.cs` sniffs only the first 64 KB of an
 * existing file before an overwrite) can land the window's end inside a character, and a truncated
 * prefix of a valid character is not evidence that the file is not UTF-8. `complete: false`
 * therefore stops the probe before such a tail; the default `true` demands that the final sequence
 * is finished, which is what every whole-file caller wants. Pass `false` ONLY for a prefix.
 */
export function isValidUtf8(bytes: Buffer, complete = true): boolean {
    if (bytes.length === 0) return true;
    const probe = complete ? bytes : trimIncompleteTail(bytes);
    if (probe.length === 0) return true;
    try {
        new TextDecoder('utf-8', { fatal: true }).decode(probe);
        return true;
    } catch {
        return false;
    }
}

// ---------------------------------------------------------------------------
// Encoding: strict, because a lossy encode is indistinguishable from corruption
// ---------------------------------------------------------------------------

export type EncodeOutcome = { ok: true; bytes: Buffer } | { ok: false; message: string };

function describeCodeUnit(text: string, index: number): string {
    const unit = text.charCodeAt(index);
    return `U+${unit.toString(16).toUpperCase().padStart(4, '0')}`;
}

/** Index of the first lone UTF-16 surrogate in `text`, or -1. */
function findLoneSurrogate(text: string): number {
    for (let i = 0; i < text.length; i++) {
        const unit = text.charCodeAt(i);
        if (unit >= 0xd800 && unit <= 0xdbff) {
            const next = i + 1 < text.length ? text.charCodeAt(i + 1) : 0;
            if (next >= 0xdc00 && next <= 0xdfff) {
                i++;
                continue;
            }
            return i;
        }
        if (unit >= 0xdc00 && unit <= 0xdfff) return i;
    }
    return -1;
}

/**
 * Encodes `text` in `charset`, FAILING instead of substituting.
 *
 * `Buffer.from(text, 'latin1')` silently writes `0x3F` ('?') for every character outside Latin-1,
 * and a lone surrogate becomes U+FFFD in UTF-8/UTF-16 — the exact corruption the writer exists to
 * prevent, only harder to see. Every charset therefore round-trips (or scans) the text first and a
 * failure names the charset and the first offending character, so the caller can fix the request.
 *
 * This is the encoder for text we GENERATE. `CharsetSpec.encode` is the byte-preserving one, used
 * where an existing file must come back unchanged — see the header.
 */
export function encodeStrict(text: string, charset: CharsetName, displayPath: string): EncodeOutcome {
    if (charset === 'latin1') {
        const bytes = Buffer.from(text, 'latin1');
        if (bytes.toString('latin1') === text) return { ok: true, bytes };

        let offender = 0;
        while (offender < text.length && text.charCodeAt(offender) <= 0xff) offender++;
        const character = offender < text.length ? text[offender] : '';
        return {
            ok: false,
            message: `File ${displayPath} cannot be written as latin1: '${character}' (${describeCodeUnit(text, offender)}) ` +
                `at index ${offender} has no latin1 representation, and substituting '?' would corrupt it. Nothing was written.`,
        };
    }

    const lone = findLoneSurrogate(text);
    if (lone >= 0) {
        return {
            ok: false,
            message: `File ${displayPath} cannot be written as ${charset}: the text contains a lone surrogate ` +
                `(${describeCodeUnit(text, lone)}) at index ${lone}, which ${charset} cannot represent without ` +
                'turning it into U+FFFD. Nothing was written.',
        };
    }

    return { ok: true, bytes: CHARSETS[charset].encode(text) };
}

// ---------------------------------------------------------------------------
// Line separators — ONE algorithm, over BYTES
// ---------------------------------------------------------------------------

/**
 * Width of one code unit, i.e. how far the separator scan advances on a non-match.
 *
 * A JSON-ish byte scan is not enough for UTF-16/UTF-32: a byte pair that STRADDLES two characters
 * (a NUL next to U+0A00 in UTF-16BE is `00 00 0A 00`) contains `00 0A`, which an unaligned search
 * would count as an LF. Stepping by the unit width keeps every comparison character-aligned.
 */
const CODE_UNIT_WIDTH: Record<CharsetName, number> = {
    'utf-8': 1,
    'utf-8-bom': 1,
    latin1: 1,
    'utf-16le': 2,
    'utf-16be': 2,
    'utf-32le': 4,
    'utf-32be': 4,
};

/**
 * The file's own separator, decided ONE way for every caller (the writer's separator for an
 * existing file, the writer's append/prepend glue, the reader's re-join separator):
 *
 *   MAJORITY of CRLF / LF / CR, ties broken CRLF > LF > CR, `undefined` when the file has no line
 *   break at all — the callers then fall back to `.editorconfig`, and finally to `'\n'`.
 *
 * Over the body's BYTES, because the encoding decides how wide a break is: a UTF-16 file holds CRLF
 * as `0D 00 0A 00`, and `body.includes(0x0A)` would be both wrong and meaningless there. CRLF is
 * matched BEFORE LF and CR, so one CRLF is one break and not two.
 *
 * The C# host applies the same rule over the same bytes (`ToolCore/FileUtils.cs`); change them
 * together. Before this, the writer used presence (`any CRLF wins`) and the reader used majority —
 * the same file could be appended to with CRLF and edited with LF.
 */
export function majoritySeparator(body: Buffer, charset: CharsetName): string | undefined {
    const spec = CHARSETS[charset];
    const crlf = spec.encode('\r\n');
    const lf = spec.encode('\n');
    const cr = spec.encode('\r');
    const width = CODE_UNIT_WIDTH[charset];

    let crlfCount = 0;
    let lfCount = 0;
    let crCount = 0;

    let offset = 0;
    while (offset < body.length) {
        if (matchesAt(body, crlf, offset)) {
            crlfCount++;
            offset += crlf.length;
        } else if (matchesAt(body, lf, offset)) {
            lfCount++;
            offset += lf.length;
        } else if (matchesAt(body, cr, offset)) {
            crCount++;
            offset += cr.length;
        } else {
            offset += width;
        }
    }

    if (crlfCount === 0 && lfCount === 0 && crCount === 0) return undefined;
    if (crlfCount >= lfCount && crlfCount >= crCount) return '\r\n';
    if (lfCount >= crCount) return '\n';
    return '\r';
}

/** Human name of a separator for a report or a note. */
export function separatorName(separator: string): string {
    if (separator === '\r\n') return 'crlf';
    if (separator === '\r') return 'cr';
    return 'lf';
}

function endsWithBytes(bytes: Buffer, suffix: Buffer): boolean {
    return bytes.length >= suffix.length && bytes.subarray(bytes.length - suffix.length).equals(suffix);
}

/**
 * True when the body already ends with a line break, so appending (or prepending) must not add a
 * second one. Checked in the file's own encoding: `0A` at the end of a UTF-16LE file is half a
 * character, not an LF.
 */
export function endsWithLineBreak(body: Buffer, charset: CharsetName): boolean {
    const spec = CHARSETS[charset];
    return endsWithBytes(body, spec.encode('\r\n')) ||
        endsWithBytes(body, spec.encode('\n')) ||
        endsWithBytes(body, spec.encode('\r'));
}
