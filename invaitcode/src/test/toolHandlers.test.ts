/**
 * Unit tests for toolHandlers.ts
 *
 * Tests pure-logic and file-system functions by mocking the 'fs' and
 * 'child_process' modules. No real I/O is performed.
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import * as path from 'path';

chai.use(chaiAsPromised);
const expect = chai.expect;

// ---------------------------------------------------------------------------
// We need to stub fs and child_process BEFORE importing toolHandlers.
// toolHandlers does `import * as fs from 'fs'` at module load time, so
// we intercept the require to return our stubbed version.
// ---------------------------------------------------------------------------

const fsStub = {
    readdirSync: sinon.stub(),
    readFileSync: sinon.stub(),
    writeFileSync: sinon.stub(),
    existsSync: sinon.stub(),
    statSync: sinon.stub(),
    unlinkSync: sinon.stub(),
    mkdirSync: sinon.stub(),
    createReadStream: sinon.stub(),
};

const childProcessStub = {
    execSync: sinon.stub(),
};

// Patch require for 'fs' and 'child_process' before loading toolHandlers.
const Module = require('module');
const originalLoad = Module._load;
Module._load = function (request: string, parent: any, isMain: boolean) {
    if (request === 'fs') return fsStub;
    if (request === 'child_process') return childProcessStub;
    return originalLoad.call(this, request, parent, isMain);
};

// Now import the module under test — it will receive our stubs.
import * as toolHandlers from '../toolHandlers';

// Restore after import
Module._load = originalLoad;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Create a fake fs.Dirent-like object. */
function dirent(name: string, isDir: boolean) {
    return {
        name,
        isDirectory: () => isDir,
        isFile: () => !isDir,
        parentPath: '/fake',
    };
}

const WORKSPACE = path.sep === '\\' ? 'B:\\ws' : '/ws';

// ---------------------------------------------------------------------------

describe('toolHandlers', () => {

    afterEach(() => {
        sinon.restore();
        // Reset all stubs
        (fsStub.readdirSync as sinon.SinonStub).reset();
        (fsStub.readFileSync as sinon.SinonStub).reset();
        (fsStub.writeFileSync as sinon.SinonStub).reset();
        (fsStub.existsSync as sinon.SinonStub).reset();
        (fsStub.statSync as sinon.SinonStub).reset();
        (fsStub.unlinkSync as sinon.SinonStub).reset();
        (fsStub.mkdirSync as sinon.SinonStub).reset();
        (childProcessStub.execSync as sinon.SinonStub).reset();
    });

    // -----------------------------------------------------------------------
    // dispatchTool
    // -----------------------------------------------------------------------

    describe('dispatchTool', () => {
        it('should return error for unknown action', () => {
            const result = toolHandlers.dispatchTool('unknown_action', '{}', WORKSPACE);
            expect(result.success).to.be.false;
            expect(result.error).to.include('Unknown action');
        });

        it('should return error for read_open_file (unknown action)', () => {
            const result = toolHandlers.dispatchTool('read_open_file', '{}', WORKSPACE);
            expect(result.success).to.be.false;
            expect(result.error).to.include('Unknown action');
        });

        it('should return error for get_error_list (unknown action)', () => {
            const result = toolHandlers.dispatchTool('get_error_list', '{}', WORKSPACE);
            expect(result.success).to.be.false;
            expect(result.error).to.include('Unknown action');
        });

        it('should handle invalid JSON payload gracefully', () => {
            const result = toolHandlers.dispatchTool('read_files', 'not json', WORKSPACE);
            expect(result.success).to.be.false;
            expect(result.error).to.be.a('string');
        });

        it('should handle undefined payload as empty object', () => {
            const result = toolHandlers.dispatchTool('get_project_info', undefined, WORKSPACE);
            expect(result.success).to.be.true;
            expect(result.payload).to.include(WORKSPACE);
        });
    });

    // -----------------------------------------------------------------------
    // buildWorkspaceTree (exercises walkDirectory internally)
    // -----------------------------------------------------------------------

    describe('buildWorkspaceTree / walkDirectory', () => {
        it('should list files and directories, skipping binary extensions and hidden/node_modules dirs', () => {
            const rootEntries = [
                dirent('src', true),
                dirent('README.md', false),
                dirent('app.exe', false),   // should be skipped (binary ext)
                dirent('image.png', false),  // should be skipped (binary ext)
                dirent('.hidden', true),     // should be skipped (hidden dir)
                dirent('node_modules', true),// should be skipped
            ];
            const srcEntries = [
                dirent('index.ts', false),
                dirent('utils', true),
            ];
            const utilsEntries = [
                dirent('helper.ts', false),
            ];

            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns(rootEntries)
                .onCall(1).returns(srcEntries)
                .onCall(2).returns(utilsEntries);

            const tree = toolHandlers.buildWorkspaceTree(WORKSPACE);

            // First line is the workspace header
            expect(tree[0]).to.include('Workspace path');
            expect(tree[0]).to.include(WORKSPACE);

            // Should include README.md and index.ts and helper.ts
            const joined = tree.join('\n');
            expect(joined).to.include('README.md');
            expect(joined).to.include('index.ts');
            expect(joined).to.include('helper.ts');

            // Should NOT include exe, png, .hidden, node_modules
            expect(joined).to.not.include('app.exe');
            expect(joined).to.not.include('image.png');
            expect(joined).to.not.include('.hidden');
            expect(joined).to.not.include('node_modules');
        });

        it('should cap files at 25 per directory and show skip count', () => {
            const manyFiles: any[] = [];
            for (let i = 0; i < 30; i++) {
                manyFiles.push(dirent(`file${i}.ts`, false));
            }
            (fsStub.readdirSync as sinon.SinonStub).returns(manyFiles);

            const tree = toolHandlers.buildWorkspaceTree(WORKSPACE);
            const joined = tree.join('\n');

            // Should show 25 files
            expect(joined).to.include('file0.ts');
            expect(joined).to.include('file24.ts');
            // Should NOT show file25+
            expect(joined).to.not.include('file25.ts');
            // Should show skip message
            expect(joined).to.include('5 more files');
        });
    });

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
    // bash / git_status / git_log / git_diff
    // -----------------------------------------------------------------------

    describe('bash', () => {
        it('should execute command and return output', () => {
            (childProcessStub.execSync as sinon.SinonStub).returns('command output');

            const payload = JSON.stringify({ command: 'echo hello' });
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.equal('command output');
            expect((childProcessStub.execSync as sinon.SinonStub).calledOnce).to.be.true;
            const callArgs = (childProcessStub.execSync as sinon.SinonStub).getCall(0).args;
            expect(callArgs[1].cwd).to.equal(WORKSPACE);
        });

        it('should return error when command is missing', () => {
            const payload = JSON.stringify({});
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('Command is required');
        });

        it('should return error on execSync failure', () => {
            const err = new Error('Command failed');
            (err as any).stderr = 'error stderr';
            (childProcessStub.execSync as sinon.SinonStub).throws(err);

            const payload = JSON.stringify({ command: 'false' });
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('error stderr');
        });
    });

    describe('git_status', () => {
        it('should delegate to bash with "git status"', () => {
            (childProcessStub.execSync as sinon.SinonStub).returns('On branch main');

            const result = toolHandlers.dispatchTool('git_status', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('On branch main');
            const cmd = (childProcessStub.execSync as sinon.SinonStub).getCall(0).args[0];
            expect(cmd).to.equal('git status');
        });
    });

    describe('git_log', () => {
        it('should call git log with specified number', () => {
            (childProcessStub.execSync as sinon.SinonStub).returns('abc123 - commit msg');

            const payload = JSON.stringify({ number: 5 });
            const result = toolHandlers.dispatchTool('git_log', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const cmd = (childProcessStub.execSync as sinon.SinonStub).getCall(0).args[0];
            expect(cmd).to.include('git log');
            expect(cmd).to.include('-n 5');
        });

        it('should default to 10 entries when number not specified', () => {
            (childProcessStub.execSync as sinon.SinonStub).returns('log');

            const result = toolHandlers.dispatchTool('git_log', '{}', WORKSPACE);

            const cmd = (childProcessStub.execSync as sinon.SinonStub).getCall(0).args[0];
            expect(cmd).to.include('-n 10');
        });

        it('should clamp number between 1 and 100', () => {
            (childProcessStub.execSync as sinon.SinonStub).returns('log');

            const payload = JSON.stringify({ number: 500 });
            toolHandlers.dispatchTool('git_log', payload, WORKSPACE);

            const cmd = (childProcessStub.execSync as sinon.SinonStub).getCall(0).args[0];
            expect(cmd).to.include('-n 100');
        });
    });

    describe('git_diff', () => {
        it('should call git diff with valid revisions', () => {
            (childProcessStub.execSync as sinon.SinonStub).returns('diff output');

            const payload = JSON.stringify({ revisions: 'HEAD~1' });
            const result = toolHandlers.dispatchTool('git_diff', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const cmd = (childProcessStub.execSync as sinon.SinonStub).getCall(0).args[0];
            expect(cmd).to.include('git diff');
            expect(cmd).to.include('HEAD~1');
        });

        it('should reject invalid revision specifications', () => {
            const payload = JSON.stringify({ revisions: 'bad; rm -rf /' });
            const result = toolHandlers.dispatchTool('git_diff', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('Invalid revision');
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
    // getAgents
    // -----------------------------------------------------------------------

    describe('getAgents', () => {
        it('should read AGENTS.md when it exists', () => {
            (fsStub.existsSync as sinon.SinonStub)
                .onCall(0).returns(true); // AGENTS.md exists
            (fsStub.readFileSync as sinon.SinonStub).returns('# Agents\nRules here');

            const result = toolHandlers.dispatchTool('get_agents', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Rules here');
        });

        it('should return error when no agents.md file found', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const result = toolHandlers.dispatchTool('get_agents', '{}', WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include("agents.md doesn't exist");
        });
    });
});
