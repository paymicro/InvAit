/**
 * Unit tests for file operation tools: readFiles, createFile, editFiles,
 * deleteFile, dir (toolHandlers/fileOps.ts).
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import { fsStub, toolHandlers, dirent, WORKSPACE, resetStubs } from './setup';
import { splitEditText } from '../../toolHandlers/diffMatch';

chai.use(chaiAsPromised);
const expect = chai.expect;

/** UTF-8 BOM bytes — an edit must carry them through, not strip them. */
const UTF8_BOM = [0xEF, 0xBB, 0xBF];

/**
 * The bytes a write answered with, as text. `edit_file` writes a Buffer now (the file's encoding
 * decides the bytes), so the old `args[1]` string comparison would silently compare a Buffer.
 */
function writtenText(callIndex = 0): string {
    const call = (fsStub.writeFileSync as sinon.SinonStub).getCall(callIndex);
    return Buffer.from(call.args[1]).toString('utf8');
}

/** The raw bytes of a write — for BOM assertions. */
function writtenBytes(callIndex = 0): Buffer {
    const call = (fsStub.writeFileSync as sinon.SinonStub).getCall(callIndex);
    return Buffer.from(call.args[1]);
}

describe('fileOps', () => {
    afterEach(() => resetStubs());

    // -----------------------------------------------------------------------
    // readFiles (via dispatchTool)
    // -----------------------------------------------------------------------

    describe('readFiles', () => {
        it('should read file content and return lines', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('line1\nline2\nline3');

            const payload = JSON.stringify({
                files: [{ path: 'src/test.txt' }]
            });
            const result = toolHandlers.dispatchTool('read_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const parsed = JSON.parse(result.payload!);
            expect(parsed).to.have.length(1);
            expect(parsed[0].path).to.equal('src/test.txt');
            expect(parsed[0].lines).to.deep.equal(['line1', 'line2', 'line3']);
        });

        it('should return error for non-existent file', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const payload = JSON.stringify({
                files: [{ path: 'missing.txt' }]
            });
            const result = toolHandlers.dispatchTool('read_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const parsed = JSON.parse(result.payload!);
            expect(parsed[0].error).to.include("doesn't exist");
        });

        it('should support startLine and lineCount', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('a\nb\nc\nd\ne\nf');

            const payload = JSON.stringify({
                files: [{ path: 'test.txt', startLine: 2, lineCount: 3 }]
            });
            const result = toolHandlers.dispatchTool('read_files', payload, WORKSPACE);

            const parsed = JSON.parse(result.payload!);
            expect(parsed[0].lines).to.deep.equal(['b', 'c', 'd']);
            expect(parsed[0].startLine).to.equal(2);
        });

        it('should deduplicate files by path (case-insensitive)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('content');

            const payload = JSON.stringify({
                files: [
                    { path: 'src/File.ts' },
                    { path: 'src/file.ts' }, // duplicate (case-insensitive)
                    { path: 'src/Other.ts' },
                ]
            });
            const result = toolHandlers.dispatchTool('read_files', payload, WORKSPACE);

            const parsed = JSON.parse(result.payload!);
            expect(parsed).to.have.length(2);
        });

        it('should keep two different ranges of the same file', () => {
            // Regression: the dedup key was the path ALONE, so a request for two ranges of one file
            // silently lost the second one and the model read only the first slice.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('a\nb\nc\nd\ne');

            const payload = JSON.stringify({
                files: [
                    { path: 'src/File.ts', startLine: 1, lineCount: 2 },
                    { path: 'src/File.ts', startLine: 4, lineCount: 2 },
                ]
            });
            const result = toolHandlers.dispatchTool('read_files', payload, WORKSPACE);

            const parsed = JSON.parse(result.payload!);
            expect(parsed).to.have.length(2);
            expect(parsed[0].lines).to.deep.equal(['a', 'b']);
            expect(parsed[1].lines).to.deep.equal(['d', 'e']);
            expect(parsed[1].startLine).to.equal(4);
        });

        it('should still dedupe the exact same range twice', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('a\nb\nc');

            const payload = JSON.stringify({
                files: [
                    { path: 'src/File.ts', startLine: 2, lineCount: 1 },
                    { path: 'src/File.ts', startLine: 2, lineCount: 1 },
                    { path: 'src/file.ts', startLine: 2, lineCount: 1 }, // same range, other case
                ]
            });
            const result = toolHandlers.dispatchTool('read_files', payload, WORKSPACE);

            expect(JSON.parse(result.payload!)).to.have.length(1);
        });
    });

    // -----------------------------------------------------------------------
    // createFile
    // -----------------------------------------------------------------------

    describe('createFile', () => {
        it('should create file with content', () => {
            (fsStub.mkdirSync as sinon.SinonStub).returns(undefined);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'src/newfile.ts',
                content: 'hello world'
            });
            const result = toolHandlers.dispatchTool('create_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('created successfully');
            expect((fsStub.writeFileSync as sinon.SinonStub).calledOnce).to.be.true;
            expect((fsStub.mkdirSync as sinon.SinonStub).calledOnce).to.be.true;
        });

        it('should REFUSE a missing content instead of truncating the file', () => {
            // An absent `content` and an empty one are different things: `overwrite` deliberately does
            // NOT treat an empty body as a no-op (creating an empty file is legal), so coercing a
            // missing field to '' truncated the existing file to zero bytes and reported success.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({ filePath: 'log.txt', mode: 'overwrite' });
            const result = toolHandlers.dispatchTool('create_file', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.equal(
                "'content' is missing: send the full file text (an empty string is a valid body). Nothing was written.");
            // The whole point: nothing reached the disk.
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });

        it('should echo a non-string content as text instead of throwing', () => {
            // The host reads `content` with GetString, which calls ToString() — a number is written
            // as its digits. The UI and this host must not disagree about what a call means.
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (fsStub.mkdirSync as sinon.SinonStub).returns(undefined);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('create_file',
                JSON.stringify({ filePath: 'n.txt', content: 5 }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(writtenText()).to.equal('5');
        });

        it('should return error on write failure', () => {
            (fsStub.mkdirSync as sinon.SinonStub).throws(new Error('Permission denied'));

            const payload = JSON.stringify({
                filePath: 'forbidden.ts',
                content: 'x'
            });
            const result = toolHandlers.dispatchTool('create_file', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('Permission denied');
        });

        it('should write utf-8 without BOM when the tree has no rule', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (fsStub.mkdirSync as sinon.SinonStub).returns(undefined);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('create_file',
                JSON.stringify({ filePath: 'new.ts', content: 'x' }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Encoding: utf-8.');
            const written = Buffer.from((fsStub.writeFileSync as sinon.SinonStub).getCall(0).args[1]);
            expect([...written]).to.deep.equal([0x78]);
        });

        it('should honour .editorconfig charset and end_of_line for a new file', () => {
            // existsSync is asked about the .editorconfig chain and the target file: only the
            // root config exists, the file does not.
            (fsStub.existsSync as sinon.SinonStub).callsFake((p: string) =>
                String(p).endsWith('.editorconfig'));
            (fsStub.readFileSync as sinon.SinonStub).returns('root = true\n[*]\ncharset = utf-8-bom\nend_of_line = crlf\n');
            (fsStub.mkdirSync as sinon.SinonStub).returns(undefined);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('create_file',
                JSON.stringify({ filePath: 'new.ts', content: 'a\nb' }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('utf-8-bom');
            expect(result.payload).to.include('Line endings: crlf.');
            const written = Buffer.from((fsStub.writeFileSync as sinon.SinonStub).getCall(0).args[1]);
            expect([...written.slice(0, 3)]).to.deep.equal([0xEF, 0xBB, 0xBF]);
            expect(written.slice(3).toString('utf8')).to.equal('a\r\nb');
        });

        it('should append to an existing file and keep its line endings', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns(Buffer.from('a\r\nb', 'utf8'));
            (fsStub.mkdirSync as sinon.SinonStub).returns(undefined);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('create_file',
                JSON.stringify({ filePath: 'log.txt', content: 'c', mode: 'append' }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('appended to');
            const written = Buffer.from((fsStub.writeFileSync as sinon.SinonStub).getCall(0).args[1]);
            // No final break in the existing file: a separator is inserted, not glued.
            expect(written.toString('utf8')).to.equal('a\r\nb\r\nc');
        });

        it('should prepend to an existing file', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns(Buffer.from('b\nc', 'utf8'));
            (fsStub.mkdirSync as sinon.SinonStub).returns(undefined);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('create_file',
                JSON.stringify({ filePath: 'log.txt', content: 'a', mode: 'prepend' }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('prepended to');
            const written = Buffer.from((fsStub.writeFileSync as sinon.SinonStub).getCall(0).args[1]);
            expect(written.toString('utf8')).to.equal('a\nb\nc');
        });

        it('should reject an unknown mode without touching the file', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.mkdirSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('create_file',
                JSON.stringify({ filePath: 'log.txt', content: 'x', mode: 'sideways' }), WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('Unknown mode');
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // editFiles (diff application)
    // -----------------------------------------------------------------------

    describe('editFiles', () => {
        it('should apply a simple single-line edit', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('old line\nsecond line');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [{
                    oldStr: 'old line',
                    newStr: 'new line',
                    approximateLine: 1
                }]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('1/1');
            const written = writtenText();
            expect(written).to.equal('new line\nsecond line');
        });

        it('should apply multi-line replacement', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns(
                'function foo() {\n  return 1;\n}\n'
            );
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [{
                    oldStr: 'function foo() {\n  return 1;\n}',
                    newStr: 'function foo() {\n  return 2;\n}',
                    approximateLine: 1
                }]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const written = writtenText();
            expect(written).to.include('return 2');
            expect(written).to.not.include('return 1');
        });

        it('should apply edits bottom-to-top (descending by approximateLine)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns(
                'line A\nline B\nline C\nline D'
            );
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [
                    { oldStr: 'line A', newStr: 'AA', approximateLine: 1 },
                    { oldStr: 'line C', newStr: 'CC', approximateLine: 3 },
                ]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('2/2');
            const written = writtenText();
            expect(written).to.equal('AA\nline B\nCC\nline D');
        });

        it('should use fuzzy matching (case-insensitive, trimmed)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('  SomeLine  \nother');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [{
                    oldStr: 'someline',
                    newStr: 'replaced',
                    approximateLine: 1
                }]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
        });

        it('should return error when no match found', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('unchanged content');

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [{
                    oldStr: 'nonexistent',
                    newStr: 'whatever',
                    approximateLine: 1
                }]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('No valid replacements');
        });

        it('should return error for non-existent file', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const payload = JSON.stringify({
                filePath: 'missing.ts',
                edits: [{ oldStr: 'a', newStr: 'b' }]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include("doesn't exist");
        });

        it('should include per-edit failure report in payload on partial success', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('line A\nline B');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [
                    { oldStr: 'line A', newStr: 'AA', approximateLine: 1 },
                    { oldStr: 'nonexistent', newStr: 'whatever', approximateLine: 2 }
                ]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            // Partial success: applied edits are NOT rolled back, but the report
            // names the failed edit (1-based position in the model's request + hint + reason)
            // so the model can re-send only the failed one.
            expect(result.success).to.be.true;
            expect(result.payload).to.include('1/2');
            expect(result.payload).to.include('Failed edits:');
            expect(result.payload).to.include('edit 2 of 2 (line 2)');
            expect(result.payload).to.include('oldStr not found');
            const written = writtenText();
            expect(written).to.equal('AA\nline B');
        });

        it('should report original edit index, not sorted position', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('line A\nline B');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            // The first edit of the REQUEST (approximateLine 5) sorts AFTER the second
            // (approximateLine 1) in the descending pass — the failure must still report the
            // request position "edit 1 of 2", not its position in the sorted copy.
            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [
                    { oldStr: 'missing-zero', newStr: 'x', approximateLine: 5 },
                    { oldStr: 'line B', newStr: 'BB', approximateLine: 1 }
                ]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('edit 1 of 2 (line 5)');
            expect(result.payload).to.not.include('edit 2 of 2');
        });

        it('should not write the file on dryRun and report would-apply count', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('line A\nline B');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                dryRun: true,
                edits: [
                    { oldStr: 'line A', newStr: 'AA', approximateLine: 1 },
                    { oldStr: 'nonexistent', newStr: 'x', approximateLine: 2 }
                ]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Dry run on test.ts');
            expect(result.payload).to.include('1/2');
            expect(result.payload).to.include('Failed edits:');
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // deleteFile
    // -----------------------------------------------------------------------

    describe('deleteFile', () => {
        it('should delete an existing file', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.unlinkSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({ path: 'temp.txt' });
            const result = toolHandlers.dispatchTool('delete_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('deleted successfully');
            expect((fsStub.unlinkSync as sinon.SinonStub).calledOnce).to.be.true;
        });

        it('should return error for non-existent file', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const payload = JSON.stringify({ path: 'nope.txt' });
            const result = toolHandlers.dispatchTool('delete_file', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('does not exist');
        });

        it('should return error on unlink failure', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.unlinkSync as sinon.SinonStub).throws(new Error('EBUSY'));

            const payload = JSON.stringify({ path: 'locked.txt' });
            const result = toolHandlers.dispatchTool('delete_file', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('EBUSY');
        });
    });

    // -----------------------------------------------------------------------
    // dir
    // -----------------------------------------------------------------------

    describe('dir', () => {
        it('should list directory contents (non-recursive)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.statSync as sinon.SinonStub).returns({ isDirectory: () => true });
            (fsStub.readdirSync as sinon.SinonStub).returns([
                dirent('file1.ts', false),
                dirent('subdir', true),
            ]);

            const payload = JSON.stringify({ path: '.', recursive: false });
            const result = toolHandlers.dispatchTool('dir', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('file1.ts');
            expect(result.payload).to.include('subdir');
        });

        it('should return error for non-existent directory', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const payload = JSON.stringify({ path: 'nonexistent' });
            const result = toolHandlers.dispatchTool('dir', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include("doesn't exist");
        });
    });

    // -----------------------------------------------------------------------
    // dir recursive
    // -----------------------------------------------------------------------

    describe('dir recursive', () => {
        it('should list directory contents recursively', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.statSync as sinon.SinonStub).returns({ isDirectory: () => true });
            // First call: root dir entries; second call: subdir entries
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([
                    dirent('file1.ts', false),
                    dirent('subdir', true),
                ])
                .onCall(1).returns([
                    dirent('nested.ts', false),
                ]);

            const payload = JSON.stringify({ path: '.', recursive: true });
            const result = toolHandlers.dispatchTool('dir', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('file1.ts');
            expect(result.payload).to.include('subdir');
            expect(result.payload).to.include('nested.ts');
        });
    });

    // -----------------------------------------------------------------------
    // hint and tolerance (indirectly via editFiles)
    // -----------------------------------------------------------------------

    describe('hint and tolerance (via editFiles)', () => {
        it('should find match with hint and tolerance (approximateLine slightly off)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            // Target line is at index 4 (line 5), hint says line 6 (off by 1, within tol=5)
            (fsStub.readFileSync as sinon.SinonStub).returns(
                'line1\nline2\nline3\nline4\ntarget line\nline6\nline7'
            );
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [{
                    oldStr: 'target line',
                    newStr: 'replaced line',
                    approximateLine: 6  // actual is line 5, within tolerance
                }]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const written = writtenText();
            expect(written).to.include('replaced line');
            expect(written).to.not.include('target line');
        });

        it('should fallback to full file search when hint doesn\'t match', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            // The target is at line 1, but hint says line 7 (way off, outside tolerance)
            // After hint fails, it should fallback to full search and find it
            (fsStub.readFileSync as sinon.SinonStub).returns(
                'find me\nline2\nline3\nline4\nline5\nline6\nline7'
            );
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [{
                    oldStr: 'find me',
                    newStr: 'found',
                    approximateLine: 7  // way off, but full search should find at line 1
                }]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const written = writtenText();
            expect(written).to.include('found');
            expect(written).to.not.include('find me');
        });
    });

    // -----------------------------------------------------------------------
    // splitEditText (line normalisation shared with the C# host)
    // -----------------------------------------------------------------------

    describe('splitEditText', () => {
        it('should split on LF', () => {
            expect(splitEditText('a\nb\nc')).to.deep.equal(['a', 'b', 'c']);
        });

        it('should split on CRLF', () => {
            expect(splitEditText('a\r\nb\r\nc')).to.deep.equal(['a', 'b', 'c']);
        });

        it('should split a mixed-ending block into lines without leftover CR', () => {
            expect(splitEditText('a\r\nb\nc\rd')).to.deep.equal(['a', 'b', 'c', 'd']);
        });

        it('should treat one trailing line break as a terminator, not an empty line', () => {
            // Regression: 'line2\n' used to become ['line2', ''] and then searched for a
            // real empty line after the target line, so the edit failed.
            expect(splitEditText('line2\n')).to.deep.equal(['line2']);
            expect(splitEditText('line2\r\n')).to.deep.equal(['line2']);
        });

        it('should keep one empty line for a second trailing break', () => {
            expect(splitEditText('a\n\n')).to.deep.equal(['a', '']);
        });

        it('should return a single empty line for empty or missing text', () => {
            expect(splitEditText('')).to.deep.equal(['']);
            expect(splitEditText(undefined)).to.deep.equal(['']);
            expect(splitEditText(null)).to.deep.equal(['']);
        });

        it('should not throw on non-string input from unvalidated JSON', () => {
            // Throwing here would escape editFiles and abort the WHOLE batch.
            expect(splitEditText(0 as any)).to.deep.equal(['']);
            expect(splitEditText(false as any)).to.deep.equal(['']);
        });

        it('should normalise a lone CR before stripping the trailing break', () => {
            expect(splitEditText('line2\r')).to.deep.equal(['line2']);
            expect(splitEditText('\r')).to.deep.equal(['']);
        });

        it('should treat a whole text of line breaks as one empty line', () => {
            expect(splitEditText('\n')).to.deep.equal(['']);
            expect(splitEditText('\r\n')).to.deep.equal(['']);
            expect(splitEditText('a\r\n\r\n')).to.deep.equal(['a', '']);
        });
    });

    describe('editFiles ambiguity and replaceAll', () => {
        it('should refuse a search that occurs in several places', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('same();\nmiddle\nsame();');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'same();', newStr: 'other();' }]
            }), WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('found 2 times');
            expect(result.error).to.include('lines 1, 3');
            expect(result.error).to.include('replaceAll: true');
            // Nothing was written: an ambiguous edit must not half-apply.
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });

        it('should disambiguate a repeated search with approximateLine', () => {
            // The duplicates must be farther apart than the tolerance (5 lines), otherwise the hint
            // window still holds both and the edit stays ambiguous — by design.
            const lines = ['same();', ...Array.from({ length: 12 }, (_, i) => `// filler ${i}`), 'same();'];
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns(lines.join('\n'));
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'same();', newStr: 'second();', approximateLine: 14 }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            const written = writtenText();
            expect(written.startsWith('same();')).to.be.true;
            expect(written.endsWith('second();')).to.be.true;
        });

        it('should stay ambiguous when the hint window still holds both duplicates', () => {
            // Tolerance is 5 lines: a 3-line file cannot be disambiguated by a hint at all.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('same();\nmiddle\nsame();');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'same();', newStr: 'second();', approximateLine: 3 }]
            }), WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('found 2 times');
        });

        it('should replace every occurrence when replaceAll is set', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('x();\nkeep\nx();\nx();');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'x();', newStr: 'y();', replaceAll: true }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            // The ONE full success sentence in this file: a success message that misreports what it
            // did is how a wrong edit goes unnoticed, so the wording itself is pinned here.
            expect(result.payload).to.equal('Changes successfully applied to test.ts. 1/1 edits, 3 replacements.');
            const written = writtenText();
            expect(written).to.equal('y();\nkeep\ny();\ny();');
        });

        it('should report how many edits and replacements were applied', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('a\nb');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [
                    { oldStr: 'a', newStr: 'A' },
                    { oldStr: 'missing', newStr: 'X' }
                ]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('1/2 edits, 1 replacements');
            // No approximateLine on this edit: the "(line ...)" part is omitted, never "line -1".
            expect(result.payload).to.include('edit 2 of 2: oldStr not found');
            expect(result.payload).to.not.include('line -1');
        });
    });

    describe('editFiles with trailing newline in oldStr', () => {
        it('should find a single line whose oldStr ends with a newline', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('line1\nline2\nline3');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'line2\n', newStr: 'replaced\n' }]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const written = writtenText();
            expect(written).to.equal('line1\nreplaced\nline3');
        });

        it('should apply a mixed-ending oldStr to a mixed-ending file', () => {
            // Mirrors the C# end-to-end test: the file and the edit carry different
            // mixes of CRLF / LF / lone CR and must meet on the same line list.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('alpha\r\nbeta\ngamma\rdelta');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'beta\ngamma\r\n', newStr: 'BETA\r\nGAMMA' }]
            });
            const result = toolHandlers.dispatchTool('edit_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const written = writtenText();
            expect(written).to.equal('alpha\r\nBETA\r\nGAMMA\r\ndelta');
        });
    });

    // -----------------------------------------------------------------------
    // editFiles: missing edit text (null oldStr matched a blank line)
    // -----------------------------------------------------------------------

    describe('editFiles missing edit text', () => {
        it('should refuse an edit whose oldStr is null instead of editing a blank line', () => {
            // Regression: splitEditText(null) returns [''] and an all-empty search legitimately
            // matches the FIRST empty line, so this edit used to insert 'x' into a blank line and
            // report success, while the VS host refused it with 'oldStr is missing'.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('alpha\n\nomega');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: null, newStr: 'x' }]
            }), WORKSPACE);

            expect(result.success).to.be.false;
            // The ONE full refusal sentence in this file: file name + the per-edit reason, joined by
            // the report header. This is the wording the model reads when the write was refused.
            expect(result.error).to.equal(
                'No valid replacements in test.ts.\nFailed edits:\n - edit 1 of 1: oldStr is missing');
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });

        it('should include the line in the label of an edit whose oldStr is missing', () => {
            // One shape for every failure: the C# twin (`EditBatch.EditLabel`) always prints
            // `edit N of M (line L)`. This host used the line-less label for a missing
            // oldStr/newStr and the with-line one for not-found/ambiguous, so the model saw the
            // same edit under two different names depending on how it failed.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('alpha\nbeta\ngamma');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: null, newStr: 'x', approximateLine: 2 }]
            }), WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.equal(
                'No valid replacements in test.ts.\nFailed edits:\n - edit 1 of 1 (line 2): oldStr is missing');
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });

        it('should refuse an edit whose newStr is null instead of deleting the matched line', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('alpha\nbeta');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'beta', newStr: null }]
            }), WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('edit 1 of 1: newStr is missing');
            expect(result.error).to.include('Send the full replacement text');
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });

        it('should still accept an explicitly empty oldStr (it addresses an empty line)', () => {
            // The guard rejects null/undefined only: '' is the legacy 'matches an empty file line'.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('alpha\n\nomega');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: '', newStr: 'filled' }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(writtenText()).to.equal('alpha\nfilled\nomega');
        });
    });

    // -----------------------------------------------------------------------
    // editFiles: the file's own properties survive the round trip
    // -----------------------------------------------------------------------

    describe('editFiles file properties', () => {
        it('should not add a final newline to a file that has none', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('a\nb');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'b', newStr: 'B' }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(writtenText()).to.equal('a\nB');
        });

        it('should keep the final newline of a file that has one', () => {
            // Regression: the reader popped the trailing empty element, dropped the flag and the
            // writer re-joined without it — every edit silently removed the file's final newline.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('a\nb\n');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'b', newStr: 'B' }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(writtenText()).to.equal('a\nB\n');
        });

        it('should keep a CR-only file CR-only', () => {
            // Regression: `includes('\r\n') ? '\r\n' : '\n'` decided the separator, so a lone-CR
            // file was rewritten to LF from top to bottom by a one-line edit.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('a\rb\r');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'a', newStr: 'A' }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(writtenText()).to.equal('A\rb\r');
        });

        it('should keep the BOM of a UTF-8 BOM file when line 1 is edited', () => {
            // Regression: the write was hard-coded to 'utf-8', which drops the BOM and, for a
            // UTF-16 file, re-encodes the decoded text as single-byte UTF-8 garbage.
            const file = Buffer.concat([Buffer.from(UTF8_BOM), Buffer.from('line1\nline2', 'utf8')]);
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns(file);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'line1', newStr: 'LINE1', approximateLine: 1 }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            const written = writtenBytes();
            expect([...written.subarray(0, 3)]).to.deep.equal(UTF8_BOM);
            expect(written.subarray(3).toString('utf8')).to.equal('LINE1\nline2');
        });

        it('should report a mixed-ending file as normalised instead of staying silent', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('a\r\nb\nc');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'b', newStr: 'B' }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            // Verbatim `EditBatch.FormatReport`: it starts on its OWN line (the failure list above
            // ends without one) and does not claim the rewrite stopped where the edit touched it —
            // the write re-joins EVERY line.
            expect(result.payload).to.include(
                "\nNote: the file had mixed line endings; it was rewritten with 'crlf' throughout.");
            expect(writtenText()).to.equal('a\r\nB\r\nc');
        });

        it('should refuse a file above the 2 MB edit limit instead of reporting oldStr not found', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.statSync as sinon.SinonStub).returns({ size: 3 * 1024 * 1024 });
            (fsStub.readFileSync as sinon.SinonStub).returns('target line');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'target line', newStr: 'replaced' }]
            }), WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('edit NOT attempted on test.ts');
            expect(result.error).to.include('file is 3.0 MB, above the 2 MB edit limit');
            expect(result.error).to.not.include('oldStr not found');
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });

        it('should refuse a binary file instead of rewriting it as UTF-8', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns(Buffer.from([0x41, 0x00, 0x42, 0x43]));
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'A', newStr: 'B' }]
            }), WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('edit NOT attempted on test.ts');
            expect(result.error).to.include('file looks binary (NUL byte in the first 1 KB)');
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });
    });

    describe('editFiles file identity', () => {
        it('should edit a BOM-less latin1 file when .editorconfig declares its charset', () => {
            // The hosts disagreed: the VS host consulted `.editorconfig` and edited the file, this
            // host refused it as binary. A tree that names the charset is the ONE case where a
            // non-UTF-8 file can be decoded without guessing, so both hosts must accept it — and
            // must write the bytes back in the SAME encoding.
            // 'id=1\n// ' + the three latin1 bytes of a Cyrillic word. The edit touches ONLY the ASCII
            // line: the real risk is that the untouched non-ASCII bytes get re-encoded.
            const latin1 = Buffer.from([0x69, 0x64, 0x3d, 0x31, 0x0a, 0x2f, 0x2f, 0x20, 0xcf, 0xf0, 0xe8, 0x0a]);
            (fsStub.existsSync as sinon.SinonStub).callsFake((p: string) =>
                String(p).endsWith('.editorconfig') || String(p).endsWith('legacy.cs'));
            (fsStub.readFileSync as sinon.SinonStub).callsFake((p: string) =>
                String(p).endsWith('.editorconfig')
                    ? 'root = true\n[*]\ncharset = latin1\n'
                    : latin1);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'legacy.cs',
                edits: [{ oldStr: 'id=1', newStr: 'id=2' }]
            }), WORKSPACE);

            expect(result.success, JSON.stringify(result)).to.be.true;
            // Byte-exact: the edited line changed and every other byte — the latin1 ones included —
            // is untouched. A UTF-8 rewrite would have replaced cf f0 e8 with ef bf bd.
            expect([...writtenBytes()]).to.deep.equal(
                [0x69, 0x64, 0x3d, 0x32, 0x0a, 0x2f, 0x2f, 0x20, 0xcf, 0xf0, 0xe8, 0x0a]);
        });

        it('should refuse a BOM-less non-UTF-8 file when no charset is declared', () => {
            (fsStub.existsSync as sinon.SinonStub).callsFake((p: string) =>
                String(p).endsWith('legacy.cs'));
            (fsStub.readFileSync as sinon.SinonStub).returns(Buffer.from([0xcf, 0xf0, 0xe8]));

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'legacy.cs',
                edits: [{ oldStr: 'a', newStr: 'b' }]
            }), WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('edit NOT attempted on legacy.cs');
            expect(result.error).to.include('not valid UTF-8');
            expect((fsStub.writeFileSync as sinon.SinonStub).called).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // editFiles: fragment (partial-line) matching and replacement
    // -----------------------------------------------------------------------

    describe('editFiles fragment matching', () => {
        it('should replace only the matched fragment and keep the line tail', () => {
            // The hosts disagreed here: whole-line-only matching found nothing in VS Code while the
            // VS host replaced the fragment — the edit either failed or landed on another line.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('if (ready) x(); // keep tight');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'x();', newStr: 'y();', approximateLine: 1 }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(writtenText()).to.equal('if (ready) y(); // keep tight');
        });

        it('should keep every trailing comment when replaceAll hits unindented lines', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('if (a) x(); // keep A\nif (b) x(); // keep B');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'x();', newStr: 'y();', replaceAll: true }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('2 replacements');
            expect(writtenText()).to.equal('if (a) y(); // keep A\nif (b) y(); // keep B');
        });

        it('should keep the tail of a truncated last boundary line', () => {
            // The last line of a multi-line block may be truncated at the END: only the matched
            // prefix is replaced and the rest of the line stays attached to the emitted line.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('function f() {\n    DoSomething(value);\n}');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{
                    oldStr: 'function f() {\n    DoSomething(val',
                    newStr: 'function f() {\n    DoSomething(val1'
                }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(writtenText()).to.equal('function f() {\n    DoSomething(val1ue);\n}');
        });

        it('should keep the tail when a single-line search starts the line', () => {
            // Reversed decision, mirroring `ApplyEdit_SearchEndingWithNewLine_FragmentAtColumnZero_KeepsTrailingComment`
            // in ToolCore.Tests: dropping the tail here made the outcome depend on indentation —
            // a column-0 fragment lost ` // keep me?` while the indented form kept it.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('DoA(); // keep me?');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: 'DoA();', newStr: 'DoB();' }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(writtenText()).to.equal('DoB(); // keep me?');
        });
    });

    // -----------------------------------------------------------------------
    // editFiles: an all-empty search block
    // -----------------------------------------------------------------------

    describe('editFiles all-empty search block', () => {
        it('should replace every line of a two-empty-line search without leaving a blank line', () => {
            // The all-empty branch removes search.length lines, not one: removing one leaves the
            // second matched blank line behind, one line below the replacement.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('a\n\n\nb');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                edits: [{ oldStr: '\n\n', newStr: 'X\nY' }]
            }), WORKSPACE);

            expect(result.success).to.be.true;
            expect(writtenText()).to.equal('a\nX\nY\nb');
        });
    });
});

/**
 * Argument tolerance: the three consumers of a call's arguments (this host, the VS host and the UI
 * badge) must agree about what the call MEANS, otherwise the user approves one thing and another
 * happens. Each test here was a real divergence found in review.
 */
describe('fileOps argument tolerance', () => {
    afterEach(() => resetStubs());

    it('should run a dry run for a tolerant truthy dryRun instead of writing the file', () => {
        // The badge the user approves says "dry run / nothing was written" and reads dryRun through
        // GetBool ("yes"/1 are true); the VS host performs the dry run for the same payload. A strict
        // `=== true` here WROTE the file while the badge claimed it was untouched.
        for (const raw of ['"yes"', '1', '"true"']) {
            resetStubs();
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('line A');
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
                filePath: 'test.ts',
                dryRun: JSON.parse(raw),
                edits: [{ oldStr: 'line A', newStr: 'AA' }]
            }), WORKSPACE);

            expect(result.success, `dryRun=${raw}`).to.be.true;
            expect(result.payload, `dryRun=${raw}`).to.include('Dry run on test.ts');
            expect((fsStub.writeFileSync as sinon.SinonStub).called, `dryRun=${raw}`).to.be.false;
        }
    });

    it('should refuse an empty edits array with the reason the VS host gives', () => {
        // `Failed to get replacements` named neither the file nor a reason.
        (fsStub.existsSync as sinon.SinonStub).returns(true);

        const result = toolHandlers.dispatchTool('edit_file',
            JSON.stringify({ filePath: 'test.ts', edits: [] }), WORKSPACE);

        expect(result.success).to.be.false;
        expect(result.error).to.equal("'edits' is empty: nothing to apply.");
    });

    it('should report a null edit as a failed edit instead of throwing', () => {
        // A JSON null inside the array is legal. The sort dereferenced `approximateLine` on it, so
        // the whole call died with a TypeError and the sibling edit never ran.
        (fsStub.existsSync as sinon.SinonStub).returns(true);
        (fsStub.readFileSync as sinon.SinonStub).returns('one');
        (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

        const result = toolHandlers.dispatchTool('edit_file', JSON.stringify({
            filePath: 'test.ts',
            edits: [null, { oldStr: 'one', newStr: 'ONE' }]
        }), WORKSPACE);

        expect(result.success).to.be.true;
        expect(result.payload).to.include(
            "edit 1 of 2: the edit is null. Each edit needs 'oldStr' and 'newStr' strings.");
        expect(writtenText()).to.equal('ONE');
    });
});
