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
        signal?: string;
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
            signal: opts.signal ?? null,
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

        it('should pass the timeout from the payload to spawnSync', () => {
            // The limit used to be hard-coded to 120000ms while the setting only controlled the
            // client wait and the badge, so the two clocks disagreed in both directions.
            setupBashStubs({ stdout: 'ok' });

            const payload = JSON.stringify({ command: 'dotnet build', timeoutMs: 45000 });
            toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            const spawnCall = (childProcessStub.spawnSync as sinon.SinonStub).getCall(0);
            expect(spawnCall.args[2].timeout).to.equal(45000);
        });

        it('should fall back to the default timeout when the payload has none', () => {
            setupBashStubs({ stdout: 'ok' });

            toolHandlers.dispatchTool('bash', JSON.stringify({ command: 'echo hi' }), WORKSPACE);

            const spawnCall = (childProcessStub.spawnSync as sinon.SinonStub).getCall(0);
            expect(spawnCall.args[2].timeout).to.equal(120000);
        });

        it('should fall back to the default timeout for a zero value', () => {
            // Channel rule: 0 is NOT "no limit" (spawnSync would drop the limit entirely) and
            // NOT "kill instantly". It means "the tool default", same as on the C# host.
            setupBashStubs({ stdout: 'ok' });

            toolHandlers.dispatchTool('bash', JSON.stringify({ command: 'echo hi', timeoutMs: 0 }), WORKSPACE);

            const call = (childProcessStub.spawnSync as sinon.SinonStub).lastCall;
            expect(call.args[2].timeout).to.equal(120000);
        });

        it('should fall back to the default timeout for a negative value', () => {
            setupBashStubs({ stdout: 'ok' });

            toolHandlers.dispatchTool('bash', JSON.stringify({ command: 'echo hi', timeoutMs: -1 }), WORKSPACE);

            const call = (childProcessStub.spawnSync as sinon.SinonStub).lastCall;
            expect(call.args[2].timeout).to.equal(120000);
        });

        it('should ignore prose that merely starts with a number', () => {
            // `parseInt` read "2 minutes" as 2 and "30s" as 30, i.e. invented a 2 ms limit out of
            // prose. Only a whole number is a limit; anything else is "no value", so the default.
            for (const timeoutMs of ['2 minutes', '30s', '4.5', '', ' ', null]) {
                setupBashStubs({ stdout: 'ok' });

                toolHandlers.dispatchTool('bash', JSON.stringify({ command: 'echo hi', timeoutMs }), WORKSPACE);

                const call = (childProcessStub.spawnSync as sinon.SinonStub).lastCall;
                expect(call.args[2].timeout, `timeoutMs=${JSON.stringify(timeoutMs)}`).to.equal(120000);
            }
        });

        it('should accept a leading plus and surrounding spaces like int.TryParse', () => {
            // `int.TryParse("+45000")` succeeds on the C# host, so rejecting it here would make the
            // same payload mean two different limits on the two hosts.
            setupBashStubs({ stdout: 'ok' });

            toolHandlers.dispatchTool('bash', JSON.stringify({ command: 'echo hi', timeoutMs: ' +45000 ' }), WORKSPACE);

            const call = (childProcessStub.spawnSync as sinon.SinonStub).lastCall;
            expect(call.args[2].timeout).to.equal(45000);
        });

        it('should fall back to the default for a value outside the int range', () => {
            // `int.TryParse("3000000000")` FAILS on the C# host, so it resolves to the tool default.
            // Clamping instead would make the same payload mean two different limits on the two
            // hosts — and the clamped value is not even holdable by a JS timer.
            for (const timeoutMs of [3_000_000_000, 2_147_483_648, 1e21]) {
                setupBashStubs({ stdout: 'ok' });

                toolHandlers.dispatchTool('bash', JSON.stringify({ command: 'echo hi', timeoutMs }), WORKSPACE);

                const call = (childProcessStub.spawnSync as sinon.SinonStub).lastCall;
                expect(call.args[2].timeout, `timeoutMs=${timeoutMs}`).to.equal(120000);
            }
        });

        it('should keep the largest representable timeout', () => {
            setupBashStubs({ stdout: 'ok' });

            toolHandlers.dispatchTool('bash', JSON.stringify({ command: 'echo hi', timeoutMs: 2_147_483_647 }), WORKSPACE);

            const call = (childProcessStub.spawnSync as sinon.SinonStub).lastCall;
            expect(call.args[2].timeout).to.equal(2_147_483_647);
        });

        it('should report the effective timeout in the timeout message', () => {
            // The message is the only reason the model sees for a killed command: it used to say
            // "after 120000ms" no matter which limit actually fired.
            const timeoutError = new Error('spawnSync ETIMEDOUT');
            (timeoutError as any).code = 'ETIMEDOUT';
            setupBashStubs({ stderr: 'partial', error: timeoutError });

            const payload = JSON.stringify({ command: 'sleep 60', timeoutMs: 5000 });
            const result = toolHandlers.dispatchTool('bash', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('Command timed out after 5000ms');
        });

        it('should keep the partial output of a command that timed out', () => {
            // Losing the log of a long build is the most expensive part of a timeout; Node does
            // report the bytes written before the kill, so dropping them is our own regression.
            const timeoutError = new Error('spawnSync ETIMEDOUT');
            (timeoutError as any).code = 'ETIMEDOUT';
            setupBashStubs({ stdout: 'partial-out', stderr: 'partial-err', error: timeoutError });

            const result = toolHandlers.dispatchTool(
                'bash', JSON.stringify({ command: 'sleep 60', timeoutMs: 5000 }), WORKSPACE);

            expect(result.error).to.include('partial-out');
            expect(result.error).to.include('partial-err');
            expect(result.error).to.include('Command timed out after 5000ms');
        });

        it('should not report an output overflow as a timeout', () => {
            // An overflow ALSO arrives with signal SIGTERM, only the code differs (ENOBUFS).
            // Classifying on the signal alone told the model the command timed out when it had
            // actually produced too much output and was killed for that.
            const overflowError = new Error('spawnSync ENOBUFS');
            (overflowError as any).code = 'ENOBUFS';
            setupBashStubs({ stderr: 'lots', error: overflowError, signal: 'SIGTERM' });

            const result = toolHandlers.dispatchTool('bash', JSON.stringify({ command: 'yes' }), WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.not.include('Command timed out');
            expect(result.error).to.include('exceeded');
            expect(result.error).to.include('lots');
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

        it('should forward the timeout to the underlying bash call', () => {
            setupBashStubs({ stdout: 'On branch main' });

            toolHandlers.dispatchTool('git_status', JSON.stringify({ timeoutMs: 30000 }), WORKSPACE);

            const spawnCall = (childProcessStub.spawnSync as sinon.SinonStub).getCall(0);
            expect(spawnCall.args[2].timeout).to.equal(30000);
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

        it('should forward the timeout to the underlying bash call', () => {
            setupBashStubs({ stdout: 'log' });

            toolHandlers.dispatchTool('git_log', JSON.stringify({ number: 2, timeoutMs: 30000 }), WORKSPACE);

            const spawnCall = (childProcessStub.spawnSync as sinon.SinonStub).getCall(0);
            expect(spawnCall.args[2].timeout).to.equal(30000);
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

        it('should forward the timeout to the underlying bash call', () => {
            setupBashStubs({ stdout: 'diff output' });

            toolHandlers.dispatchTool('git_diff', JSON.stringify({ revisions: 'HEAD', timeoutMs: 30000 }), WORKSPACE);

            const spawnCall = (childProcessStub.spawnSync as sinon.SinonStub).getCall(0);
            expect(spawnCall.args[2].timeout).to.equal(30000);
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
