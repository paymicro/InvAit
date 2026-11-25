/**
 * Unit tests for the shared encoding primitives (toolHandlers/encoding.ts).
 *
 * No fs stubs and no `./setup` import on purpose: the module is pure by contract, and that is
 * exactly what these tests prove — every assertion here is about BYTES, because the defects this
 * pins are invisible at the string level (`Buffer.toString('utf8')` reports nothing while it
 * substitutes U+FFFD, `TextDecoder` without `fatal` accepts everything).
 */
import * as chai from 'chai';
import {
    CHARSETS,
    CREATABLE_CHARSETS,
    EMPTY_BUFFER,
    encodeStrict,
    endsWithLineBreak,
    isCreatableCharset,
    isValidUtf8,
    majoritySeparator,
    separatorName,
    sniffPreamble,
} from '../../toolHandlers/encoding';

const expect = chai.expect;

describe('encoding', () => {
    // -----------------------------------------------------------------------
    // BOM ladder
    // -----------------------------------------------------------------------
    describe('sniffPreamble', () => {
        it('should read FF FE 00 00 as utf-32le, not as utf-16le', () => {
            const sniffed = sniffPreamble(Buffer.from([0xff, 0xfe, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00]));

            expect(sniffed?.spec.charset).to.equal('utf-32le');
            // Four bytes of preamble: testing UTF-16LE first would leave the second NUL in the body.
            expect(sniffed?.bodyOffset).to.equal(4);
        });

        it('should read 00 00 FE FF as utf-32be', () => {
            const sniffed = sniffPreamble(Buffer.from([0x00, 0x00, 0xfe, 0xff, 0x00, 0x00, 0x00, 0x41]));

            expect(sniffed?.spec.charset).to.equal('utf-32be');
            expect(sniffed?.bodyOffset).to.equal(4);
        });

        it('should read EF BB BF as utf-8-bom', () => {
            const sniffed = sniffPreamble(Buffer.from([0xef, 0xbb, 0xbf, 0x61]));

            expect(sniffed?.spec.charset).to.equal('utf-8-bom');
            expect(sniffed?.bodyOffset).to.equal(3);
        });

        it('should read FF FE as utf-16le when it is not a UTF-32LE BOM', () => {
            const sniffed = sniffPreamble(Buffer.from([0xff, 0xfe, 0x61, 0x00]));

            expect(sniffed?.spec.charset).to.equal('utf-16le');
            expect(sniffed?.bodyOffset).to.equal(2);
        });

        it('should read FE FF as utf-16be', () => {
            const sniffed = sniffPreamble(Buffer.from([0xfe, 0xff, 0x00, 0x61]));

            expect(sniffed?.spec.charset).to.equal('utf-16be');
            expect(sniffed?.bodyOffset).to.equal(2);
        });

        it('should report no preamble for a BOM-less file and for an empty one', () => {
            expect(sniffPreamble(Buffer.from('plain', 'utf8'))).to.be.undefined;
            expect(sniffPreamble(Buffer.alloc(0))).to.be.undefined;
        });

        it('should decode the body from bodyOffset for a BOM-ed utf-16 file', () => {
            const bytes = Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('a\r\nb', 'utf16le')]);
            const sniffed = sniffPreamble(bytes);

            expect(sniffed).to.not.be.undefined;
            expect(sniffed!.spec.decode(bytes.subarray(sniffed!.bodyOffset))).to.equal('a\r\nb');
        });
    });

    // -----------------------------------------------------------------------
    // Strict UTF-8 probe
    // -----------------------------------------------------------------------
    describe('isValidUtf8', () => {
        it('should accept empty, ASCII and multi-byte UTF-8', () => {
            expect(isValidUtf8(Buffer.alloc(0))).to.be.true;
            expect(isValidUtf8(Buffer.from('plain ascii', 'utf8'))).to.be.true;
            expect(isValidUtf8(Buffer.from('Привет — ok', 'utf8'))).to.be.true;
        });

        it('should reject a latin1/cp1251 body and a stray 0xFF byte', () => {
            // The exact bytes the old `toString('utf8')` decode turned into U+FFFD garbage.
            expect(isValidUtf8(Buffer.from([0xcf, 0xf0, 0xe8]))).to.be.false;
            expect(isValidUtf8(Buffer.from([0xff]))).to.be.false;
        });

        it('should reject a truncated multi-byte sequence', () => {
            const cyrillic = Buffer.from('Привет', 'utf8');
            // 12 bytes for 6 characters: dropping the last byte leaves a half sequence.
            expect(isValidUtf8(cyrillic.subarray(0, cyrillic.length - 1))).to.be.false;
            expect(isValidUtf8(Buffer.from([0xd0]))).to.be.false;
        });

        it('should tolerate exactly that truncation when the bytes are only a window', () => {
            // `complete: false` is what the writer's 64 KB sniff passes: the window can cut the
            // LAST character in half, and a half character at the edge is not evidence that the
            // file is not UTF-8. Anything the window cut that is NOT a valid lead is still
            // rejected — otherwise a real non-UTF-8 file would be silently re-encoded.
            const cyrillic = Buffer.from('Привет', 'utf8');
            expect(isValidUtf8(cyrillic.subarray(0, cyrillic.length - 1), false)).to.be.true;
            expect(isValidUtf8(Buffer.from([0xd0]), false)).to.be.true;
            expect(isValidUtf8(Buffer.from([0xff]), false)).to.be.false;
            expect(isValidUtf8(Buffer.from([0xcf, 0xf0, 0xe8]), false)).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // Strict encoder: refusal instead of substitution
    // -----------------------------------------------------------------------
    describe('encodeStrict', () => {
        it('should refuse Cyrillic in latin1 instead of writing question marks', () => {
            const outcome = encodeStrict('Привет', 'latin1', 'notes.txt');

            expect(outcome.ok).to.be.false;
            const message = outcome.ok ? '' : outcome.message;
            expect(message).to.include('File notes.txt cannot be written as latin1');
            expect(message).to.include('U+041F');
            expect(message).to.include('Nothing was written.');
        });

        it('should encode characters latin1 does hold', () => {
            const outcome = encodeStrict('café', 'latin1', 'notes.txt');

            expect(outcome.ok).to.be.true;
            expect(outcome.ok ? [...outcome.bytes] : []).to.deep.equal([0x63, 0x61, 0x66, 0xe9]);
        });

        it('should refuse a lone surrogate in every UTF charset', () => {
            const outcome = encodeStrict('a\uD800b', 'utf-16le', 'a.txt');

            expect(outcome.ok).to.be.false;
            const message = outcome.ok ? '' : outcome.message;
            expect(message).to.include('lone surrogate');
            expect(message).to.include('U+D800');
            expect(message).to.include('index 1');
            expect(message).to.include('utf-16le');
        });

        it('should accept a well-formed surrogate pair', () => {
            const outcome = encodeStrict('a\uD83D\uDE00b', 'utf-8', 'a.txt');

            expect(outcome.ok).to.be.true;
            // 'a' + the 4-byte emoji + 'b'.
            expect(outcome.ok ? outcome.bytes.length : 0).to.equal(6);
        });

        it('should NOT emit the preamble: the caller prepends it', () => {
            const outcome = encodeStrict('x', 'utf-8-bom', 'a.ts');

            expect(outcome.ok ? [...outcome.bytes] : []).to.deep.equal([0x78]);
        });
    });

    // -----------------------------------------------------------------------
    // The two encoders are deliberate: `CharsetSpec.encode` is byte-preserving
    // -----------------------------------------------------------------------
    describe('CHARSETS', () => {
        it('should round-trip an unpaired surrogate byte-for-byte in utf-16le', () => {
            // `encodeStrict` refuses this text; the round trip MUST reproduce it, otherwise reading
            // and writing a file unchanged would edit it.
            const text = 'a\uD800b';
            const bytes = CHARSETS['utf-16le'].encode(text);

            expect([...bytes]).to.deep.equal([0x61, 0x00, 0x00, 0xd8, 0x62, 0x00]);
            expect(CHARSETS['utf-16le'].decode(bytes)).to.equal(text);
            expect(encodeStrict(text, 'utf-16le', 'a.txt').ok).to.be.false;
        });

        it('should expose an empty preamble for the BOM-less charsets', () => {
            expect(CHARSETS['utf-8'].preamble.length).to.equal(0);
            expect(CHARSETS.latin1.preamble.length).to.equal(0);
            expect(EMPTY_BUFFER.length).to.equal(0);
            expect([...CHARSETS['utf-8-bom'].preamble]).to.deep.equal([0xef, 0xbb, 0xbf]);
        });

        it('should list exactly the charsets a tree may create', () => {
            expect([...CREATABLE_CHARSETS]).to.deep.equal(['utf-8', 'utf-8-bom', 'latin1', 'utf-16le', 'utf-16be']);
            expect(isCreatableCharset('latin1')).to.be.true;
            // UTF-32 is detection-only: a file that has the BOM keeps it, the tree never starts one.
            expect(isCreatableCharset('utf-32le')).to.be.false;
            expect(isCreatableCharset('utf-7')).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // Majority separator: ONE algorithm, over BYTES
    // -----------------------------------------------------------------------
    describe('majoritySeparator', () => {
        it('should pick the most frequent break, not the first one it finds', () => {
            const body = Buffer.from('a\r\nb\nc\nd\ne', 'utf8');

            expect(majoritySeparator(body, 'utf-8')).to.equal('\n');
        });

        it('should break a CRLF/LF tie in favour of CRLF', () => {
            expect(majoritySeparator(Buffer.from('a\r\nb\nc', 'utf8'), 'utf-8')).to.equal('\r\n');
        });

        it('should break an LF/CR tie in favour of LF', () => {
            expect(majoritySeparator(Buffer.from('a\nb\rc', 'utf8'), 'utf-8')).to.equal('\n');
        });

        it('should return the only kind of break a file has', () => {
            expect(majoritySeparator(Buffer.from('a\rb', 'utf8'), 'utf-8')).to.equal('\r');
            expect(majoritySeparator(Buffer.from('a\r\nb', 'utf8'), 'utf-8')).to.equal('\r\n');
            expect(majoritySeparator(Buffer.from('a\nb', 'utf8'), 'utf-8')).to.equal('\n');
        });

        it('should return undefined when the body has no line break at all', () => {
            expect(majoritySeparator(Buffer.from('abc', 'utf8'), 'utf-8')).to.be.undefined;
            expect(majoritySeparator(Buffer.alloc(0), 'utf-8')).to.be.undefined;
        });

        it('should count CRLF as ONE break in a utf-16 body', () => {
            expect(majoritySeparator(Buffer.from('a\r\nb', 'utf16le'), 'utf-16le')).to.equal('\r\n');
        });

        it('should apply the majority rule to a utf-16 body', () => {
            expect(majoritySeparator(Buffer.from('a\r\nb\nc\nd\ne', 'utf16le'), 'utf-16le')).to.equal('\n');
        });

        it('should count breaks in a utf-32 body', () => {
            const body = CHARSETS['utf-32le'].encode('a\r\nb\nc\nd\ne');
            expect(majoritySeparator(body, 'utf-32le')).to.equal('\n');
        });

        it('should not read a byte pair straddling two characters as an LF', () => {
            // U+0000 then U+0A00 in UTF-16BE is `00 00 0A 00`: the unaligned pair `00 0A` at offset 1
            // is NOT a line break, and a byte-presence algorithm would invent one.
            const body = Buffer.from([0x00, 0x00, 0x0a, 0x00]);

            expect(majoritySeparator(body, 'utf-16be')).to.be.undefined;
        });
    });

    describe('separatorName', () => {
        it('should name all three separators', () => {
            expect(separatorName('\r\n')).to.equal('crlf');
            expect(separatorName('\n')).to.equal('lf');
            expect(separatorName('\r')).to.equal('cr');
        });
    });

    describe('endsWithLineBreak', () => {
        it('should recognise each break in utf-8', () => {
            expect(endsWithLineBreak(Buffer.from('a\r\n', 'utf8'), 'utf-8')).to.be.true;
            expect(endsWithLineBreak(Buffer.from('a\n', 'utf8'), 'utf-8')).to.be.true;
            expect(endsWithLineBreak(Buffer.from('a\r', 'utf8'), 'utf-8')).to.be.true;
            expect(endsWithLineBreak(Buffer.from('a', 'utf8'), 'utf-8')).to.be.false;
            expect(endsWithLineBreak(Buffer.alloc(0), 'utf-8')).to.be.false;
        });

        it('should match the break in the file own encoding, not as ASCII', () => {
            expect(endsWithLineBreak(Buffer.from('a\r\n', 'utf16le'), 'utf-16le')).to.be.true;
            expect(endsWithLineBreak(Buffer.from('a', 'utf16le'), 'utf-16le')).to.be.false;
            // `61 00 00 0A` ends with `00 0A`: a UTF-16LE LF is `0A 00`, so this is not a break.
            expect(endsWithLineBreak(Buffer.from([0x61, 0x00, 0x00, 0x0a]), 'utf-16le')).to.be.false;
        });
    });
});
