/**
 * Unit tests for bash and git tools: bash, git_status, git_log, git_diff,
 * and truncateOutput (toolHandlers/bash.ts, toolHandlers/fileUtils.ts).
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import { fsStub, childProcessStub, toolHandlers, WORKSPACE, resetStubs } from './setup';

chai.use(chaiAsPromised);
const expect = chai.expect;

describe('bash', () => {
    afterEach(() => resetStubs());

    /**
     * Helper: set up stubs so that findGitSh() succeeds and spawnSync returns
     * the given stdout/stderr/status.  findGitSh needs existsSync to return
     * true for at least one path it checks.
     */
    function setupBashStubs(opts: {
        stdout?: string;
        stderr?: string;
        status?: number;
        error?: Error;
    }) {
        // findGitSh: existsSync returns true so sh.exe is "found" in PATH
        (fsStub.existsSync as sinon.SinonStub).returns(true);
        (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);
        (fsStub.unlinkSync as sinon.SinonStub).returns(undefined);
        (childProcessStub.spawnSync as sinon.SinonStub).returns({
            stdout: opts.stdout ?? '',
            stderr: opts.stderr ?? '',
            status: opts.status ?? 0,
            error: opts.error,
        });
    }

    // -----------------------------------------------------------------------
    // bash
    // -----------------------------------------------------------------------

    describe('bash', () => {
        it('should execute command and return output', () => {
            setupBashStubs({ stdout: 'command output' });

            const payload = JSON.stringify({ command: 'echo hello' });
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.equal('command output');
            expect((childProcessStub.spawnSync as sinon.SinonStub).calledOnce).to.be.true;
            const callArgs = (childProcessStub.spawnSync as sinon.SinonStub).getCall(0).args;
            expect(callArgs[2].cwd).to.equal(WORKSPACE);
        });

        it('should return error when command is missing', () => {
            const payload = JSON.stringify({});
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('Command is required');
        });

        it('should return error on non-zero exit code', () => {
            setupBashStubs({ stdout: '', stderr: 'error stderr', status: 1 });

            const payload = JSON.stringify({ command: 'false' });
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('error stderr');
        });

        it('should return error when sh.exe is not found', () => {
            // existsSync returns false for everything, execSync throws
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (childProcessStub.execSync as sinon.SinonStub).throws(new Error('not found'));

            const payload = JSON.stringify({ command: 'echo hello' });
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('sh not found');
        });

        it('should strip temp file path from error output', () => {
            // existsSync returns true so findGitSh succeeds
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.writeFileSync as sinon.SinonStub).returns(undefined);
            (fsStub.unlinkSync as sinon.SinonStub).returns(undefined);
            // spawnSync returns stderr containing the ACTUAL temp file path
            // (captured from the call arguments) to verify stripping works
            (childProcessStub.spawnSync as sinon.SinonStub).callsFake(
                (_shPath: string, args: string[]) => ({
                    stdout: '',
                    stderr: `${args[0]}: line 1: cd: /no/such/dir: No such file or directory`,
                    status: 1,
                    error: undefined,
                })
            );

            const payload = JSON.stringify({ command: 'cd /no/such/dir' });
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.false;
            // The temp file path should NOT appear in the error
            expect(result.error).to.not.include('invait_');
            expect(result.error).to.not.include('.sh');
            expect(result.error).to.include('line 1');
        });

        it('should write command to temp file and pass it to spawnSync', () => {
            setupBashStubs({ stdout: 'ok' });

            const payload = JSON.stringify({ command: 'echo test' });
            toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            // writeFileSync should be called with the command as content
            const writeCall = (fsStub.writeFileSync as sinon.SinonStub).getCall(0);
            expect(writeCall.args[1]).to.equal('echo test');
            // spawnSync should be called with [tempScriptPath] as args
            const spawnCall = (childProcessStub.spawnSync as sinon.SinonStub).getCall(0);
            expect(spawnCall.args[1]).to.be.an('array').with.length(1);
            expect(spawnCall.args[1][0]).to.include('.sh');
        });
    });

    // -----------------------------------------------------------------------
    // git_status
    // -----------------------------------------------------------------------

    describe('git_status', () => {
        it('should delegate to bash with "git status"', () => {
            setupBashStubs({ stdout: 'On branch main' });

            const result = toolHandlers.dispatchTool('git_status', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('On branch main');
            // The command written to temp file should be "git status"
            const writeCall = (fsStub.writeFileSync as sinon.SinonStub).getCall(0);
            expect(writeCall.args[1]).to.equal('git status');
        });
    });

    // -----------------------------------------------------------------------
    // git_log
    // -----------------------------------------------------------------------

    describe('git_log', () => {
        it('should call git log with specified number', () => {
            setupBashStubs({ stdout: 'abc123 - commit msg' });

            const payload = JSON.stringify({ number: 5 });
            const result = toolHandlers.dispatchTool('git_log', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const writeCall = (fsStub.writeFileSync as sinon.SinonStub).getCall(0);
            const cmd = writeCall.args[1];
            expect(cmd).to.include('git log');
            expect(cmd).to.include('-n 5');
        });

        it('should default to 10 entries when number not specified', () => {
            setupBashStubs({ stdout: 'log' });

            const result = toolHandlers.dispatchTool('git_log', '{}', WORKSPACE);

            const writeCall = (fsStub.writeFileSync as sinon.SinonStub).getCall(0);
            expect(writeCall.args[1]).to.include('-n 10');
        });

        it('should clamp number between 1 and 100', () => {
            setupBashStubs({ stdout: 'log' });

            const payload = JSON.stringify({ number: 500 });
            toolHandlers.dispatchTool('git_log', payload, WORKSPACE);

            const writeCall = (fsStub.writeFileSync as sinon.SinonStub).getCall(0);
            expect(writeCall.args[1]).to.include('-n 100');
        });
    });

    // -----------------------------------------------------------------------
    // git_diff
    // -----------------------------------------------------------------------

    describe('git_diff', () => {
        it('should call git diff with valid revisions', () => {
            setupBashStubs({ stdout: 'diff output' });

            const payload = JSON.stringify({ revisions: 'HEAD~1' });
            const result = toolHandlers.dispatchTool('git_diff', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const writeCall = (fsStub.writeFileSync as sinon.SinonStub).getCall(0);
            expect(writeCall.args[1]).to.include('git diff');
            expect(writeCall.args[1]).to.include('HEAD~1');
        });

        it('should reject invalid revision specifications', () => {
            const payload = JSON.stringify({ revisions: 'bad; rm -rf /' });
            const result = toolHandlers.dispatchTool('git_diff', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('Invalid revision');
        });
    });

    // -----------------------------------------------------------------------
    // truncateOutput (indirectly via bash)
    // -----------------------------------------------------------------------

    describe('truncateOutput (via bash)', () => {
        it('should truncate output longer than 30000 characters', () => {
            // Generate output > 30000 chars
            const longOutput = 'x'.repeat(35000);
            setupBashStubs({ stdout: longOutput });

            const payload = JSON.stringify({ command: 'echo big' });
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.true;
            // Should contain truncation message
            expect(result.payload).to.include('characters truncated');
            // Payload should be shorter than the original
            expect(result.payload!.length).to.be.lessThan(longOutput.length);
        });
    });
});
