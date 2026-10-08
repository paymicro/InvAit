/**
 * Unit tests for file operation tools: readFiles, createFile, editFiles,
 * deleteFile, dir (toolHandlers/fileOps.ts).
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import { fsStub, toolHandlers, dirent, WORKSPACE, resetStubs } from './setup';

chai.use(chaiAsPromised);
const expect = chai.expect;

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

        it('should default content to empty string when not provided', () => {
            (fsStub.mkdirSync as sinon.SinonStub).returns(undefined);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);

            const payload = JSON.stringify({ filePath: 'empty.txt' });
            const result = toolHandlers.dispatchTool('create_file', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const writeCall = (fsStub.writeFileSync as sinon.SinonStub).getCall(0);
            expect(writeCall.args[1]).to.equal('');
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
            const result = toolHandlers.dispatchTool('edit_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('1/1');
            const written = (fsStub.writeFileSync as sinon.SinonStub).getCall(0).args[1];
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
            const result = toolHandlers.dispatchTool('edit_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const written = (fsStub.writeFileSync as sinon.SinonStub).getCall(0).args[1];
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
            const result = toolHandlers.dispatchTool('edit_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('2/2');
            const written = (fsStub.writeFileSync as sinon.SinonStub).getCall(0).args[1];
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
            const result = toolHandlers.dispatchTool('edit_files', payload, WORKSPACE);

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
            const result = toolHandlers.dispatchTool('edit_files', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('No valid replacements');
        });

        it('should return error for non-existent file', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const payload = JSON.stringify({
                filePath: 'missing.ts',
                edits: [{ oldStr: 'a', newStr: 'b' }]
            });
            const result = toolHandlers.dispatchTool('edit_files', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include("doesn't exist");
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
    // findInFile edge cases (indirectly via editFiles)
    // -----------------------------------------------------------------------

    describe('findInFile edge cases (via editFiles)', () => {
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
            const result = toolHandlers.dispatchTool('edit_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const written = (fsStub.writeFileSync as sinon.SinonStub).getCall(0).args[1];
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
            const result = toolHandlers.dispatchTool('edit_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const written = (fsStub.writeFileSync as sinon.SinonStub).getCall(0).args[1];
            expect(written).to.include('found');
            expect(written).to.not.include('find me');
        });
    });
});
