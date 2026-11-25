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
import { fsStub, toolHandlers, WORKSPACE, resetStubs } from './setup';

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
