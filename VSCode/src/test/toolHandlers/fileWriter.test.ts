/**
 * Unit tests for the `create_file` writer (toolHandlers/fileWriter.ts).
 *
 * These tests are BYTE assertions on purpose. The defect they cover is invisible at the string level:
 * a cp1251/latin1 file with no BOM used to be read with `toString('utf8')` and written back, which
 * replaced every non-ASCII byte with EF BF BD (U+FFFD) while the report still claimed
 * "existing bytes preserved". Anything less than the exact written buffer would not catch it.
 */
import * as chai from 'chai';
import * as sinon from 'sinon';
import * as path from 'path';
import { fsStub, resetStubs } from './setup';
import { parseMode, writeFileWithMode } from '../../toolHandlers/fileWriter';
import { editFiles } from '../../toolHandlers/fileOps';

const expect = chai.expect;

// Loaded directly (a namespace import returns a copy whose stubbing is inert) and after './setup',
// which restores `Module._load` at the end of its own body.
import fs = require('fs');

const ROOT = path.resolve('fake-writer-root');

/** Absolute path of a file inside the virtual tree. */
function node(relative: string): string {
    return path.join(ROOT, relative);
}

interface VirtualTree {
    /** Every path handed to `readFileSync`, in order. */
    reads: string[];
    /** Every successful `writeFileSync`, with the exact bytes. */
    writes: { path: string; bytes: Buffer }[];
}

/**
 * Points BOTH `fs` module identities at one virtual tree.
 *
 * `fileWriter.ts` / `editorConfig.ts` keep a late-bound view of the `fs` object captured at load time,
 * and which object that is depends on the order mocha loads the test files — `setup.ts` swaps 'fs' for
 * `fsStub` before importing the toolHandlers barrel, so it is usually `fsStub`, but not guaranteed.
 * Patching both keeps these tests independent of that order.
 */
function stubTree(files: Record<string, Buffer | string>): VirtualTree {
    const tree: VirtualTree = { reads: [], writes: [] };

    const exists = (p: unknown): boolean => Object.prototype.hasOwnProperty.call(files, String(p));

    const read = (p: unknown): Buffer | string => {
        const key = String(p);
        tree.reads.push(key);
        if (!Object.prototype.hasOwnProperty.call(files, key)) {
            const error = new Error(`ENOENT: no such file or directory, open '${key}'`) as Error & { code?: string };
            error.code = 'ENOENT';
            throw error;
        }
        return files[key];
    };

    const write = (p: unknown, data: unknown): void => {
        const bytes = Buffer.from(data as Buffer);
        tree.writes.push({ path: String(p), bytes });
        files[String(p)] = bytes;
    };

    const mkdir = (): undefined => undefined;

    (fsStub.existsSync as sinon.SinonStub).callsFake(exists);
    (fsStub.readFileSync as sinon.SinonStub).callsFake(read);
    (fsStub.writeFileSync as sinon.SinonStub).callsFake(write);
    (fsStub.mkdirSync as sinon.SinonStub).callsFake(mkdir);

    (sinon.stub(fs, 'existsSync') as unknown as sinon.SinonStub).callsFake(exists);
    (sinon.stub(fs, 'readFileSync') as unknown as sinon.SinonStub).callsFake(read);
    (sinon.stub(fs, 'writeFileSync') as unknown as sinon.SinonStub).callsFake(write);
    (sinon.stub(fs, 'mkdirSync') as unknown as sinon.SinonStub).callsFake(mkdir);

    return tree;
}

/** cp1251 'ПРИ' — not valid UTF-8, no BOM: exactly the file the old writer destroyed. */
const LATIN1_NO_BOM = Buffer.from([0xcf, 0xf0, 0xe8]);

const BOM_UTF8 = [0xef, 0xbb, 0xbf];

describe('fileWriter writeFileWithMode', () => {
    afterEach(() => resetStubs());

    // -----------------------------------------------------------------------
    // A non-UTF-8 file without a BOM: refuse, never re-encode
    // -----------------------------------------------------------------------

    it('should refuse to append to a non-UTF-8 file with no BOM and no declared charset', () => {
        const target = node('notes.txt');
        const tree = stubTree({ [target]: LATIN1_NO_BOM });

        const result = writeFileWithMode('notes.txt', target, 'x', 'append');

        expect(result.success).to.be.false;
        expect(result.payload).to.equal(
            'File notes.txt is not valid UTF-8 and has no BOM, and .editorconfig declares no charset. ' +
            'Refusing to rewrite it — non-ASCII bytes would be lost. ' +
            'Set charset in .editorconfig, or edit the file with bash.');
        // The whole point: nothing was written, so no EF BF BD can appear anywhere.
        expect(tree.writes).to.have.length(0);
    });

    it('should refuse to overwrite a non-UTF-8 file with no BOM and no declared charset', () => {
        const target = node('notes.txt');
        const tree = stubTree({ [target]: LATIN1_NO_BOM });

        const result = writeFileWithMode('notes.txt', target, 'plain ascii', 'overwrite');

        expect(result.success).to.be.false;
        expect(tree.writes).to.have.length(0);
    });

    it('should keep a latin1 body byte-for-byte when the tree declares the charset', () => {
        const target = node('notes.txt');
        const tree = stubTree({
            [node('.editorconfig')]: '[*]\ncharset = latin1\nend_of_line = crlf\n',
            [target]: LATIN1_NO_BOM,
        });

        const result = writeFileWithMode('notes.txt', target, 'ok', 'append');

        expect(result.success).to.be.true;
        expect(result.payload).to.include('Encoding: latin1 (from .editorconfig).');
        expect(tree.writes).to.have.length(1);
        // Existing bytes untouched, then the separator, then the new latin1 text.
        expect([...tree.writes[0].bytes]).to.deep.equal([0xcf, 0xf0, 0xe8, 0x0d, 0x0a, 0x6f, 0x6b]);
    });

    it('should fail instead of substituting question marks when latin1 cannot hold the text', () => {
        const target = node('notes.txt');
        const tree = stubTree({
            [node('.editorconfig')]: '[*]\ncharset = latin1\n',
            [target]: LATIN1_NO_BOM,
        });

        const result = writeFileWithMode('notes.txt', target, 'Привет', 'append');

        expect(result.success).to.be.false;
        expect(result.payload).to.include('latin1');
        expect(result.payload).to.include('U+041F');
        expect(tree.writes).to.have.length(0);
    });

    // -----------------------------------------------------------------------
    // Detection order
    // -----------------------------------------------------------------------

    it('should treat FF FE 00 00 as UTF-32LE, not as UTF-16LE', () => {
        const target = node('wide.txt');
        // BOM_UTF32LE + 'A' as one code point: FF FE 00 00 | 41 00 00 00.
        const tree = stubTree({ [target]: Buffer.from([0xff, 0xfe, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00]) });

        const result = writeFileWithMode('wide.txt', target, 'B', 'append');

        expect(result.payload).to.include('Encoding: utf-32le.');
        // Appending UTF-16 (`42 00`) here is the old bug: the new text must be four bytes per point.
        expect([...tree.writes[0].bytes]).to.deep.equal([
            0xff, 0xfe, 0x00, 0x00,
            0x41, 0x00, 0x00, 0x00,
            0x0a, 0x00, 0x00, 0x00,
            0x42, 0x00, 0x00, 0x00,
        ]);
    });

    it('should round-trip a utf-8-bom file byte-exactly when appending', () => {
        const target = node('bom.txt');
        const tree = stubTree({ [target]: Buffer.concat([Buffer.from(BOM_UTF8), Buffer.from('a\r\nb', 'utf8')]) });

        writeFileWithMode('bom.txt', target, 'c', 'append');

        expect(tree.writes[0].bytes.equals(
            Buffer.concat([Buffer.from(BOM_UTF8), Buffer.from('a\r\nb\r\nc', 'utf8')]))).to.be.true;
    });

    it('should round-trip a utf-16le file byte-exactly when appending', () => {
        const target = node('wide16.txt');
        const tree = stubTree({ [target]: Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('a\r\nb', 'utf16le')]) });

        const result = writeFileWithMode('wide16.txt', target, 'c', 'append');

        expect(result.payload).to.include('Encoding: utf-16le.');
        expect(tree.writes[0].bytes.equals(
            Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('a\r\nb\r\nc', 'utf16le')]))).to.be.true;
    });

    // -----------------------------------------------------------------------
    // Composition and separators
    // -----------------------------------------------------------------------

    it('should use lf when an existing file has no line break at all', () => {
        const target = node('notes.txt');
        const tree = stubTree({ [target]: Buffer.from('abc', 'utf8') });

        writeFileWithMode('notes.txt', target, 'd', 'append');

        // A literal '\n' — never os.EOL, which would put CRLF on a Windows host into a lone-line file.
        expect(tree.writes[0].bytes.toString('utf8')).to.equal('abc\nd');
    });

    it('should use the MAJORITY separator of an existing file, not the first one present', () => {
        // One CRLF and three LFs. The old presence sniff answered CRLF because it asked
        // `includes('\r\n')` first, so an append here and an edit_file of the same file disagreed
        // about the newline they added. Both now ask the same majority question.
        const target = node('mixed.txt');
        const tree = stubTree({ [target]: Buffer.from('a\r\nb\nc\nd\ne', 'utf8') });

        const result = writeFileWithMode('mixed.txt', target, 'f', 'append');

        // No `.editorconfig` here, so no rule was applied: the report must stay silent about line
        // endings. The bytes below are what proves the majority answer.
        expect(result.payload).to.not.include('Line endings:');
        expect(tree.writes[0].bytes.toString('utf8')).to.equal('a\r\nb\nc\nd\ne\nf');
    });

    it('should break a CRLF/LF tie in favour of CRLF when appending', () => {
        const target = node('mixed.txt');
        const tree = stubTree({ [target]: Buffer.from('a\r\nb\nc', 'utf8') });

        const result = writeFileWithMode('mixed.txt', target, 'd', 'append');

        expect(result.payload).to.not.include('Line endings:');
        expect(tree.writes[0].bytes.toString('utf8')).to.equal('a\r\nb\nc\r\nd');
    });

    it('should count line breaks in a utf-16 file as code units and glue with its own bytes', () => {
        // A UTF-16LE file holds CRLF as 0D 00 0A 00, so the majority question and the glue both
        // have to be answered in the file's encoding — an ASCII '\r\n' here would be two bytes of
        // garbage wedged between two UTF-16 characters.
        const target = node('wide16.txt');
        const tree = stubTree({ [target]: Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('a\r\nb\nc', 'utf16le')]) });

        const result = writeFileWithMode('wide16.txt', target, 'd', 'append');

        expect(result.payload).to.not.include('Line endings:');
        expect(tree.writes[0].bytes.equals(
            Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('a\r\nb\nc\r\nd', 'utf16le')]))).to.be.true;
    });

    it('should keep an existing latin1 file byte-for-byte and add no glue when it ends with a break', () => {
        // BOM-less and not valid UTF-8: the tree names the charset, so the body is trusted as-is and
        // never passes through a decoder. The body is the cp1251 bytes CF F0 E8 plus an LF; the
        // appended 'ok' is ASCII, which latin1 and UTF-8 spell the same way, so those high bytes are
        // the proof that nothing was re-encoded.
        const target = node('notes.txt');
        const tree = stubTree({
            [node('.editorconfig')]: '[*]\ncharset = latin1\n',
            [target]: Buffer.from([0xcf, 0xf0, 0xe8, 0x0a]),
        });

        const result = writeFileWithMode('notes.txt', target, 'ok', 'append');

        expect(result.success).to.be.true;
        expect(result.payload).to.include('Encoding: latin1 (from .editorconfig).');
        expect(tree.writes).to.have.length(1);
        expect([...tree.writes[0].bytes]).to.deep.equal([0xcf, 0xf0, 0xe8, 0x0a, 0x6f, 0x6b]);
    });

    it('should prepend verbatim into an empty existing file', () => {
        const target = node('empty.txt');
        const tree = stubTree({ [target]: Buffer.alloc(0) });

        const result = writeFileWithMode('empty.txt', target, 'a', 'prepend');

        expect(result.success).to.be.true;
        expect(tree.writes[0].bytes.toString('utf8')).to.equal('a');
        expect(tree.writes[0].bytes.length).to.equal(1);
    });

    it('should keep the preamble at the FRONT when prepending to a BOM file', () => {
        // `chunk + fileBytes` would bury the BOM in the middle of the file, where nothing
        // recognises it and every consumer reads it as stray text. Mirror of the C# writer,
        // which always emits `preamble + …`.
        const target = node('bom.txt');
        const tree = stubTree({ [target]: Buffer.concat([Buffer.from(BOM_UTF8), Buffer.from('old\n', 'utf8')]) });

        const result = writeFileWithMode('bom.txt', target, 'new', 'prepend');

        expect(result.success).to.be.true;
        const written = tree.writes[0].bytes;
        expect(written.subarray(0, 3)).to.deep.equal(Buffer.from(BOM_UTF8));
        expect(written.toString('utf8')).to.equal('\uFEFFnew\nold\n');
    });

    it('should keep the file preamble and replace the body on overwrite', () => {
        const target = node('bom.txt');
        const tree = stubTree({ [target]: Buffer.concat([Buffer.from(BOM_UTF8), Buffer.from('old\r\n', 'utf8')]) });

        const result = writeFileWithMode('bom.txt', target, 'new', 'overwrite');

        expect([...tree.writes[0].bytes]).to.deep.equal([...BOM_UTF8, 0x6e, 0x65, 0x77]);
        // No `.editorconfig` value to disagree with: no note, and therefore no note tail either.
        expect(result.payload).to.not.include('Note:');
        expect(result.payload).to.not.include('The written content used');
    });

    it('should report the outcome with an exact payload', () => {
        const target = node('notes.txt');
        stubTree({ [target]: Buffer.from('a\r\nb', 'utf8') });

        const result = writeFileWithMode('src/notes.txt', target, 'c', 'append');

        // The bare success sentence: no `.editorconfig` rule was applied, so neither `Line endings:`
        // nor a note tail may appear. This is the model-facing contract for a plain append.
        expect(result.payload).to.equal('File src/notes.txt appended to successfully. Encoding: utf-8.');
    });

    it('should report a charset mismatch and say which part of the file the setting applied to', () => {
        // The tree asks for `utf-8` (no BOM) while the file already HAS a BOM. The writer refuses to
        // silently strip it — that would be a re-encode the caller never asked for — so it says so,
        // and then says what the setting DID apply to. The tail exists ONLY inside a note: printing
        // it on every append made the two hosts describe the same write differently.
        const target = node('bom.txt');
        stubTree({
            [target]: Buffer.concat([Buffer.from(BOM_UTF8), Buffer.from('old\n', 'utf8')]),
            [node('.editorconfig')]: '[*]\ncharset = utf-8\n',
        });

        const result = writeFileWithMode('bom.txt', target, 'new', 'append');

        expect(result.payload).to.include(
            "Note: .editorconfig requires charset 'utf-8' but the file is 'utf-8-bom'.");
        expect(result.payload).to.include(
            "The existing bytes were left untouched; only the new text used 'utf-8-bom'.");
        expect(result.payload).to.not.include('Line endings:');
    });

    it('should not write anything for an empty append', () => {
        const target = node('log.txt');
        const tree = stubTree({ [target]: Buffer.from('a\n', 'utf8') });

        const result = writeFileWithMode('log.txt', target, '', 'append');

        expect(result.success).to.be.true;
        expect(result.payload).to.equal(
            'Nothing to append to log.txt: the content was empty and the file was not modified.');
        expect(tree.writes).to.have.length(0);
    });

    it('should not write anything for an empty prepend', () => {
        const target = node('log.txt');
        const tree = stubTree({ [target]: Buffer.from('a\n', 'utf8') });

        const result = writeFileWithMode('log.txt', target, '', 'prepend');

        expect(result.success).to.be.true;
        expect(result.payload).to.include('was not modified');
        expect(tree.writes).to.have.length(0);
    });

    // -----------------------------------------------------------------------
    // New files
    // -----------------------------------------------------------------------

    it('should strip a leading U+FEFF when the target charset means no BOM', () => {
        const target = node('a.ts');
        const tree = stubTree({});

        const result = writeFileWithMode('a.ts', target, '\uFEFFconst a = 1;', 'overwrite');

        expect(tree.writes[0].bytes.toString('utf8')).to.equal('const a = 1;');
        expect(result.payload).to.include(
            "Note: a leading U+FEFF was removed because charset 'utf-8' means no BOM.");
    });

    it('should write the preamble of a utf-8-bom tree and keep the content verbatim', () => {
        const target = node('a.ts');
        const tree = stubTree({ [node('.editorconfig')]: '[*]\ncharset = utf-8-bom\n' });

        writeFileWithMode('a.ts', target, 'x', 'overwrite');

        expect([...tree.writes[0].bytes]).to.deep.equal([...BOM_UTF8, 0x78]);
    });

    it('should refuse a lone surrogate instead of emitting U+FFFD', () => {
        const target = node('a.txt');
        const tree = stubTree({ [node('.editorconfig')]: '[*]\ncharset = utf-16le\n' });

        const result = writeFileWithMode('a.txt', target, 'a\uD800b', 'overwrite');

        expect(result.success).to.be.false;
        expect(result.payload).to.include('lone surrogate');
        expect(tree.writes).to.have.length(0);
    });

    it('should name an unsupported charset instead of silently using it', () => {
        const target = node('a.txt');
        const tree = stubTree({ [node('.editorconfig')]: '[*]\ncharset = utf-7\n' });

        const result = writeFileWithMode('a.txt', target, 'x', 'overwrite');

        expect(result.success).to.be.true;
        expect(result.payload).to.include("Note: charset 'utf-7' is not supported, wrote 'utf-8' instead.");
        expect([...tree.writes[0].bytes]).to.deep.equal([0x78]);
    });

    // -----------------------------------------------------------------------
    // The overwrite SNIFF WINDOW (64 KB, mirror of ToolCore/FileWriter.cs)
    // -----------------------------------------------------------------------

    it('should decide an overwrite from the first 64 KB only, so the head picks the newline', () => {
        // A file whose head is LF-only and whose tail is CRLF-only. Counting the WHOLE file finds
        // 10 000 LF against 40 000 CRLF and answers CRLF; counting the window the C# writer also
        // counts finds 10 000 LF and no CRLF at all and answers LF. Both hosts wrote a different
        // newline for the same call — this is the assertion that the window is honoured.
        const head = Buffer.concat([
            Buffer.alloc(10_000, 0x0a), // LF after LF, nothing else
            Buffer.alloc(64 * 1024 - 10_000, 0x61), // 'a' padding up to exactly the window
        ]);
        const tail = Buffer.from('b\r\n'.repeat(40_000), 'utf8'); // CRLF-heavy, past the window
        const tree = stubTree({ [node('big.txt')]: Buffer.concat([head, tail]) });

        const result = writeFileWithMode('big.txt', node('big.txt'), 'x\r\ny', 'overwrite');

        expect(result.success, JSON.stringify(result)).to.be.true;
        // The written content is normalised to the separator the WINDOW chose: LF here, CRLF if the
        // tail had been counted.
        expect(tree.writes[0].bytes.toString('utf8')).to.equal('x\ny');
    });
});

// ---------------------------------------------------------------------------
// An edit whose NEW text the file's charset cannot hold: refuse, never write '?'
// ---------------------------------------------------------------------------

describe('editFiles write-through encoding', () => {
    afterEach(() => resetStubs());

    it('should refuse an edit whose new text latin1 cannot hold and leave the file untouched', () => {
        // The file reads fine — the tree names its charset — and the edit MATCHES, so the refusal
        // can only come from the WRITE side. `Buffer.from(text, 'latin1')` writes one 0x3F ('?') per
        // Cyrillic letter and reports "Changes successfully applied": bytes on disk that no reader
        // can turn back into the requested text.
        const target = node('legacy.cs');
        // 'id=1\n' then the cp1251 bytes CF F0 E8 ('ПРИ'), which make the file non-UTF-8 — the only
        // reason the tree's `charset = latin1` gets to name the encoding.
        const tree = stubTree({
            [node('.editorconfig')]: 'root = true\n[*]\ncharset = latin1\n',
            [target]: Buffer.from([0x69, 0x64, 0x3d, 0x31, 0x0a, 0xcf, 0xf0, 0xe8]),
        });

        const result = editFiles(
            { filePath: 'legacy.cs', edits: [{ oldStr: 'id=1', newStr: 'id=2 привет' }] },
            ROOT);

        expect(result.success, JSON.stringify(result)).to.be.false;
        // The encodeStrict wording, unchanged: it names the charset and the first offending character.
        expect(result.error).to.include('cannot be written as latin1');
        expect(result.error).to.include('U+043F');
        // …and it is the WRITE that refused, not the read: the file's own bytes decoded losslessly.
        expect(result.error).to.not.include('file is not valid');
        // Nothing was written, so the six '?' never reached the disk.
        expect(tree.writes).to.have.length(0);
    });

    describe('parseMode', () => {
        // The VS Code host cannot consume the C# contract, so this is the ONLY other copy of the
        // alias table (`Shared/Contracts/FileWriteModeNames.cs` is the .NET one). These cases mirror
        // `ToolCore.Tests/FileWriteModeNamesTests.cs` exactly: an alias added on one side must be
        // added on the other in the same change, and this block is what makes a miss visible.
        const accepted: Array<[unknown, string]> = [
            [null, 'overwrite'],
            [undefined, 'overwrite'],
            ['', 'overwrite'],
            ['overwrite', 'overwrite'],
            ['create', 'overwrite'],
            ['replace', 'overwrite'],
            ['append', 'append'],
            ['APPEND', 'append'],
            [' append ', 'append'],
            ['Prepend', 'prepend'],
        ];

        for (const [input, expected] of accepted) {
            it(`resolves ${JSON.stringify(input)} to ${expected}`, () => {
                expect(parseMode(input)).to.equal(expected);
            });
        }

        for (const rejected of ['sideways', 'app', 'overwrit']) {
            it(`rejects ${JSON.stringify(rejected)}`, () => {
                expect(() => parseMode(rejected)).to.throw(/Unknown mode/);
            });
        }
    });
});

/**
 * Who owns a leading `U+FEFF` in the CONTENT. The file's BOM belongs to the resolved ENCODING, so a
 * U+FEFF arriving as text is always a second one. These cases were live defects in this host while
 * the C# writer already handled them: the guard fired only for a charset without a BOM, and the
 * existing-file path stripped nothing at all.
 */
describe('fileWriter U+FEFF ownership', () => {
    afterEach(() => resetStubs());

    it('should strip a U+FEFF from the content of a NEW file in a utf-8-bom tree', () => {
        // Preamble PLUS the content's character was two BOMs in a row — a compile error before the
        // first token, and invisible in any string-level assertion.
        const target = node('a.ts');
        const tree = stubTree({ [node('.editorconfig')]: '[*]\ncharset = utf-8-bom\n' });

        const result = writeFileWithMode('a.ts', target, '\uFEFFx', 'overwrite');

        expect([...tree.writes[0].bytes]).to.deep.equal([...BOM_UTF8, 0x78]);
        expect(result.payload).to.include('a leading U+FEFF was removed');
    });

    it('should strip a U+FEFF from the content of an EXISTING BOM-less file', () => {
        // Nothing stripped it here: the file gained an EF BB BF it never had.
        const target = node('a.ts');
        const tree = stubTree({ [target]: Buffer.from('using System;', 'utf8') });

        const result = writeFileWithMode('a.ts', target, '\uFEFFusing System;', 'overwrite');

        expect([...tree.writes[0].bytes]).to.deep.equal([...Buffer.from('using System;', 'utf8')]);
        expect(result.payload).to.include('a leading U+FEFF was removed');
    });

    it('should not double the preamble of an existing BOM file when the content carries a U+FEFF', () => {
        const target = node('bom.ts');
        const tree = stubTree({ [target]: Buffer.concat([Buffer.from(BOM_UTF8), Buffer.from('old', 'utf8')]) });

        writeFileWithMode('bom.ts', target, '\uFEFFnew', 'overwrite');

        const written = tree.writes[0].bytes;
        expect([...written.subarray(0, 3)]).to.deep.equal(BOM_UTF8);
        expect(written.subarray(3).toString('utf8')).to.equal('new');
    });

    it('should be a no-op when the appended content is only a U+FEFF', () => {
        // Stripped BEFORE the empty-content guard so the guard sees an empty body. After it, the
        // guard passed on "\uFEFF", the chunk encoded to nothing, and the glue still grew the file.
        const target = node('a.txt');
        const tree = stubTree({ [target]: Buffer.from('a', 'utf8') });

        const result = writeFileWithMode('a.txt', target, '\uFEFF', 'append');

        expect(result.success).to.be.true;
        expect(result.payload).to.equal(
            'Nothing to append to a.txt: the content was empty and the file was not modified.');
        expect(tree.writes).to.have.length(0);
    });

    it('should not print Line endings when the declared value is not the one the file uses', () => {
        // `Line endings:` states the rule this write ENFORCED. Printing the sniffed value next to
        // "requires 'crlf' but the file uses 'lf'" made the payload contradict itself.
        const target = node('a.txt');
        stubTree({
            [node('.editorconfig')]: '[*]\nend_of_line = crlf\n',
            [target]: Buffer.from('a\nb', 'utf8'),
        });

        const result = writeFileWithMode('a.txt', target, 'x\ny', 'overwrite');

        expect(result.payload).to.not.include('Line endings:');
        expect(result.payload).to.include("requires end_of_line 'crlf' but the file uses 'lf'");
    });
});
