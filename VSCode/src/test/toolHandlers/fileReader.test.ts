/**
 * Unit tests for the READ side of the edit round trip (toolHandlers/fileReader.ts).
 *
 * The defect these tests cover cannot be seen in the text a decode returns: Node never throws on
 * bad bytes. `toString('utf8')` substitutes U+FFFD, `utf16le` silently drops an odd trailing byte
 * and UTF-32 clamps an out-of-range unit — so the old reader handed a garbled line list to the
 * matcher and the write put those replacement bytes back into the file, reporting success. The
 * reader now re-encodes what it decoded and REFUSES when the bytes do not come back.
 *
 * Hence: a refusal, and NOT one single write. Anything weaker (asserting the payload mentions the
 * file, say) would pass even while the file is being destroyed.
 */
import * as chai from 'chai';
import * as sinon from 'sinon';
import * as path from 'path';
import { fsStub, toolHandlers, WORKSPACE, resetStubs, stubFsTree } from './setup';
import { decodeTextBytes, decodeWorkspaceFileBytes } from '../../toolHandlers/fileReader';
import { CHARSETS } from '../../toolHandlers/encoding';

const expect = chai.expect;

/** An edit that would apply to the files below, so the READ refusal is the only thing under test. */
function editRequest(filePath: string): string {
    return JSON.stringify({ filePath, edits: [{ oldStr: 'a', newStr: 'A' }] });
}

describe('fileReader decode fidelity', () => {
    afterEach(() => resetStubs());

    it('should refuse a UTF-8 BOM file whose body holds a bare 0xFF byte', () => {
        // The BOM names the charset, so no probe can save this: 0xFF is not a UTF-8 byte at all and
        // `toString('utf8')` answers U+FFFD. Writing that text back would put EF BF BD where the
        // file had FF — a byte the file never contained.
        const file = Buffer.concat([
            Buffer.from([0xef, 0xbb, 0xbf]),
            Buffer.from([0x61, 0x0a, 0xff, 0x0a, 0x62]), // 'a\n' + FF + '\nb'
        ]);
        (fsStub.existsSync as sinon.SinonStub).returns(true);
        (fsStub.readFileSync as sinon.SinonStub).returns(file);
        (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

        const result = toolHandlers.dispatchTool('edit_file', editRequest('test.txt'), WORKSPACE);

        expect(result.success, JSON.stringify(result)).to.be.false;
        expect(result.error).to.include('edit NOT attempted on test.txt');
        expect(result.error).to.include('file is not valid utf-8-bom');
        expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
    });

    it('should refuse a UTF-16LE file with an odd trailing byte', () => {
        // `utf16le` decodes code unit by code unit and drops the last odd byte without a word, so
        // the re-encode is one byte shorter. Accepting it meant the next successful edit deleted
        // that byte — the file changed by being opened.
        const file = Buffer.concat([
            Buffer.from([0xff, 0xfe]),
            Buffer.from('a\nb', 'utf16le'),
            Buffer.from([0x41]), // one byte of a code unit, cut off
        ]);
        (fsStub.existsSync as sinon.SinonStub).returns(true);
        (fsStub.readFileSync as sinon.SinonStub).returns(file);
        (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

        const result = toolHandlers.dispatchTool('edit_file', editRequest('test.txt'), WORKSPACE);

        expect(result.success, JSON.stringify(result)).to.be.false;
        expect(result.error).to.include('file is not valid utf-16le');
        expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
    });
});

/**
 * The shared bytes-to-text ladder, tested on its own — it is the one thing `read_files` and
 * `edit_file` now have in common, so a regression here is a regression in BOTH tools.
 */
describe('decodeTextBytes', () => {
    it('should decode UTF-32LE by its BOM', () => {
        // The longest BOM prefix wins. Reading `FF FE 00 00` as UTF-16LE is what once spliced
        // UTF-16 bytes into a UTF-32 file on the next write.
        const file = Buffer.concat([
            Buffer.from([0xff, 0xfe, 0x00, 0x00]),
            CHARSETS['utf-32le'].encode('a\nb'),
        ]);

        const decoded = decodeTextBytes(file);

        expect(decoded.ok).to.be.true;
        if (!decoded.ok) return;
        expect(decoded.text).to.equal('a\nb');
        expect(decoded.spec.charset).to.equal('utf-32le');
    });

    it('should refuse a BOM-less buffer that is not valid UTF-8', () => {
        // Node answers `a\uFFFDb` and reports nothing; a decode its own encoder cannot undo is the
        // only proof available that the bytes are not UTF-8.
        const decoded = decodeTextBytes(Buffer.from([0x61, 0xff, 0x62]));

        expect(decoded.ok).to.be.false;
        if (decoded.ok) return;
        expect(decoded.reason).to.include('file is not valid utf-8');
    });

    it('should use the declared charset for a BOM-less buffer', () => {
        // The branch `edit_file` uses for a latin1 file named in `.editorconfig`. `read_files` never
        // passes it, which is why it is a PARAMETER here and not part of the ladder itself.
        const decoded = decodeTextBytes(Buffer.from([0x61, 0xe9]), 'latin1');

        expect(decoded.ok).to.be.true;
        if (!decoded.ok) return;
        expect(decoded.text).to.equal('a\u00e9');
        expect(decoded.spec.charset).to.equal('latin1');
    });

    it('should let the BOM win over a declared charset', () => {
        // A file that names itself is identified by its own bytes: the declaration describes what the
        // tree ASKS FOR, and a mismatch is the write side's note, not the read side's guess.
        const file = Buffer.concat([
            Buffer.from([0xef, 0xbb, 0xbf]),
            Buffer.from('a', 'utf8'),
        ]);

        const decoded = decodeTextBytes(file, 'latin1');

        expect(decoded.ok).to.be.true;
        if (!decoded.ok) return;
        expect(decoded.text).to.equal('a');
        expect(decoded.spec.charset).to.equal('utf-8-bom');
    });
});

/**
 * The DECISION both file tools share: `decodeTextBytes` plus the two inputs a caller cannot invent
 * on its own — the NUL probe and the tree's charset. A regression here makes `read_files` and
 * `edit_file` disagree about the same file, which is the defect this shared function removes.
 */
describe('decodeWorkspaceFileBytes', () => {
    afterEach(() => resetStubs());

    it('should probe a BOM-less file for NUL bytes before trusting the decode', () => {
        // `ab` in UTF-16LE is `61 00 62 00`: every byte is ASCII, so the strict-UTF-8 probe and the
        // decode/re-encode check both agree, and the text would arrive with a NUL after every
        // character. That is why the probe exists, and why it runs only for a file WITHOUT a BOM.
        const decoded = decodeWorkspaceFileBytes(path.join(WORKSPACE, 'utf16.txt'), Buffer.from('ab', 'utf16le'));

        expect(decoded.ok).to.be.false;
        if (decoded.ok) return;
        expect(decoded.reason).to.include('file looks binary');
    });

    it('should not probe a BOM-ed file for NUL bytes', () => {
        // UTF-16 text is FULL of NULs — probing first would declare every UTF-16 file binary.
        const file = Buffer.concat([
            Buffer.from([0xff, 0xfe]),
            Buffer.from('ab', 'utf16le'),
        ]);

        const decoded = decodeWorkspaceFileBytes(path.join(WORKSPACE, 'utf16.txt'), file);

        expect(decoded.ok).to.be.true;
        if (!decoded.ok) return;
        expect(decoded.text).to.equal('ab');
        expect(decoded.spec.charset).to.equal('utf-16le');
    });

    it('should use the charset .editorconfig declares for a BOM-less file', () => {
        // A cp1251/latin1 file in a tree that names its charset. Without this fallback the same file
        // is editable through `edit_file` and unreadable through `read_files` — one host, two answers.
        const root = path.resolve('fake-editorconfig-root');
        const absPath = path.join(root, 'file.txt');
        const bytes = Buffer.from([0x61, 0xe9]); // 'a' + 0xE9, which is not a UTF-8 lead
        // Only the config needs to exist: `decodeWorkspaceFileBytes` takes the bytes as an argument
        // and asks the tree one question — which charset it declares for that path.
        stubFsTree({
            [path.join(root, '.editorconfig')]: 'root = true\n[*.txt]\ncharset = latin1\n',
        });

        const decoded = decodeWorkspaceFileBytes(absPath, bytes);

        expect(decoded.ok).to.be.true;
        if (!decoded.ok) return;
        expect(decoded.text).to.equal('a\u00e9');
        expect(decoded.spec.charset).to.equal('latin1');
    });

    it('should refuse a BOM-less non-UTF-8 file when the tree declares no charset', () => {
        const root = path.resolve('fake-editorconfig-root');
        const absPath = path.join(root, 'file.bin');
        const bytes = Buffer.from([0x61, 0xe9]);
        stubFsTree({});

        const decoded = decodeWorkspaceFileBytes(absPath, bytes);

        expect(decoded.ok).to.be.false;
        if (decoded.ok) return;
        expect(decoded.reason).to.include('.editorconfig declares no charset');
    });
});
