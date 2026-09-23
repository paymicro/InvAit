/**
 * Unit tests for logger.ts
 *
 * Tests the OutputChannel lifecycle (init, dispose), logging functions
 * (log, logError, logWarning), and the showOutput function. The 'vscode'
 * module is intercepted to return the mock from vscodeMock.ts.
 */
import * as chai from 'chai';
import * as sinon from 'sinon';

const expect = chai.expect;

// ---------------------------------------------------------------------------
// Intercept Module._load so that `import * as vscode from 'vscode'` inside
// logger.ts resolves to our vscodeMock instead of the real extension.
// ---------------------------------------------------------------------------
const Module = require('module');
const originalLoad = Module._load;
const path = require('path');

Module._load = function (request: string, parent: any, isMain: boolean) {
    if (request === 'vscode') {
        return originalLoad.call(this, path.resolve(__dirname, 'vscodeMock.ts'), parent, isMain);
    }
    return originalLoad.call(this, request, parent, isMain);
};

// Import logger AFTER setting up the interception so it receives the mock.
import { initLogger, getChannel, log, logError, logWarning, showOutput, disposeLogger } from '../logger';

// Restore Module._load after import.
Module._load = originalLoad;

// Import the mock output channel directly for assertions.
import { mockOutputChannel, window as mockWindow } from './vscodeMock';

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('logger', () => {

    let sandbox: sinon.SinonSandbox;

    beforeEach(() => {
        sandbox = sinon.createSandbox();
    });

    afterEach(() => {
        // Restore only stubs created via the sandbox (console.log etc.).
        sandbox.restore();
        // Reset logger module-level state so each test starts fresh.
        disposeLogger();
        // Reset the vscode mock stubs' call history (use resetHistory, not reset,
        // so that createOutputChannel still returns mockOutputChannel).
        (mockWindow.createOutputChannel as sinon.SinonStub).resetHistory();
        mockOutputChannel.appendLine.resetHistory();
        mockOutputChannel.show.resetHistory();
        mockOutputChannel.dispose.resetHistory();
    });

    // -----------------------------------------------------------------------
    // initLogger
    // -----------------------------------------------------------------------

    describe('initLogger', () => {
        it('should create an OutputChannel named "InvAit"', () => {
            initLogger();

            expect((mockWindow.createOutputChannel as sinon.SinonStub).calledOnce).to.be.true;
            expect((mockWindow.createOutputChannel as sinon.SinonStub).calledWith('InvAit')).to.be.true;
        });

        it('should not create a second channel if already initialized (idempotent)', () => {
            initLogger();
            initLogger();

            expect((mockWindow.createOutputChannel as sinon.SinonStub).calledOnce).to.be.true;
        });
    });

    // -----------------------------------------------------------------------
    // getChannel
    // -----------------------------------------------------------------------

    describe('getChannel', () => {
        it('should return null before initLogger', () => {
            expect(getChannel()).to.be.null;
        });

        it('should return the channel after initLogger', () => {
            initLogger();

            const ch = getChannel();
            expect(ch).to.not.be.null;
            expect(ch).to.equal(mockOutputChannel);
        });

        it('should return null after disposeLogger', () => {
            initLogger();
            expect(getChannel()).to.not.be.null;

            disposeLogger();

            expect(getChannel()).to.be.null;
        });
    });

    // -----------------------------------------------------------------------
    // log
    // -----------------------------------------------------------------------

    describe('log', () => {
        it('should call channel.appendLine with a string containing the message and a [HH:MM:SS] timestamp', () => {
            initLogger();

            const consoleStub = sandbox.stub(console, 'log');
            log('hello world');

            expect(mockOutputChannel.appendLine.calledOnce).to.be.true;
            const line = mockOutputChannel.appendLine.getCall(0).args[0] as string;
            expect(line).to.include('hello world');
            // Timestamp in [HH:MM:SS] or [H:MM:SS AM/PM] format (locale-dependent)
            expect(line).to.match(/^\[\d{1,2}:\d{2}:\d{2}/);
            expect(consoleStub.calledOnce).to.be.true;
        });

        it('should also call console.log', () => {
            initLogger();

            const consoleStub = sandbox.stub(console, 'log');
            log('test message');

            expect(consoleStub.calledOnce).to.be.true;
            const logged = consoleStub.getCall(0).args[0] as string;
            expect(logged).to.include('test message');
        });

        it('should not throw when channel is null (before init)', () => {
            // Ensure channel is null — do NOT call initLogger.
            disposeLogger();

            const consoleStub = sandbox.stub(console, 'log');
            // Should not throw — channel is null so appendLine is skipped via ?.
            expect(() => log('no channel')).to.not.throw();
            expect(mockOutputChannel.appendLine.called).to.be.false;
            // console.log is still called
            expect(consoleStub.calledOnce).to.be.true;
        });
    });

    // -----------------------------------------------------------------------
    // logError
    // -----------------------------------------------------------------------

    describe('logError', () => {
        it('should call channel.appendLine with [ERROR] prefix', () => {
            initLogger();

            const consoleStub = sandbox.stub(console, 'error');
            logError('something broke');

            expect(mockOutputChannel.appendLine.calledOnce).to.be.true;
            const line = mockOutputChannel.appendLine.getCall(0).args[0] as string;
            expect(line).to.include('[ERROR]');
            expect(line).to.include('something broke');
            // Timestamp should still be present
            expect(line).to.match(/^\[\d{1,2}:\d{2}:\d{2}/);
        });

        it('should call console.error', () => {
            initLogger();

            const consoleStub = sandbox.stub(console, 'error');
            logError('failure');

            expect(consoleStub.calledOnce).to.be.true;
            const logged = consoleStub.getCall(0).args[0] as string;
            expect(logged).to.include('[ERROR]');
            expect(logged).to.include('failure');
        });
    });

    // -----------------------------------------------------------------------
    // logWarning
    // -----------------------------------------------------------------------

    describe('logWarning', () => {
        it('should call channel.appendLine with [WARN] prefix', () => {
            initLogger();

            const consoleStub = sandbox.stub(console, 'warn');
            logWarning('be careful');

            expect(mockOutputChannel.appendLine.calledOnce).to.be.true;
            const line = mockOutputChannel.appendLine.getCall(0).args[0] as string;
            expect(line).to.include('[WARN]');
            expect(line).to.include('be careful');
            // Timestamp should still be present
            expect(line).to.match(/^\[\d{1,2}:\d{2}:\d{2}/);
        });

        it('should call console.warn', () => {
            initLogger();

            const consoleStub = sandbox.stub(console, 'warn');
            logWarning('caution');

            expect(consoleStub.calledOnce).to.be.true;
            const logged = consoleStub.getCall(0).args[0] as string;
            expect(logged).to.include('[WARN]');
            expect(logged).to.include('caution');
        });
    });

    // -----------------------------------------------------------------------
    // showOutput
    // -----------------------------------------------------------------------

    describe('showOutput', () => {
        it('should call channel.show(true)', () => {
            initLogger();

            showOutput();

            expect(mockOutputChannel.show.calledOnce).to.be.true;
            expect(mockOutputChannel.show.calledWith(true)).to.be.true;
        });

        it('should not throw when channel is null', () => {
            // Ensure channel is null — do NOT call initLogger.
            disposeLogger();

            // Should not throw — channel is null so show is skipped via ?.
            expect(() => showOutput()).to.not.throw();
            expect(mockOutputChannel.show.called).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // disposeLogger
    // -----------------------------------------------------------------------

    describe('disposeLogger', () => {
        it('should call channel.dispose()', () => {
            initLogger();

            disposeLogger();

            expect(mockOutputChannel.dispose.calledOnce).to.be.true;
        });

        it('should set getChannel() to null after dispose', () => {
            initLogger();
            expect(getChannel()).to.not.be.null;

            disposeLogger();

            expect(getChannel()).to.be.null;
        });

        it('should not throw when calling log() after dispose (channel is null, ?. operator)', () => {
            initLogger();
            disposeLogger();

            const consoleStub = sandbox.stub(console, 'log');
            // Should not throw — channel is null so appendLine is skipped via ?.
            expect(() => log('after dispose')).to.not.throw();
            expect(mockOutputChannel.appendLine.called).to.be.false;
            // console.log is still called
            expect(consoleStub.calledOnce).to.be.true;
        });

        it('should be safe to call when channel is null', () => {
            // Ensure channel is null — do NOT call initLogger.
            disposeLogger();

            // Should not throw — channel?.dispose() is skipped via ?.
            expect(() => disposeLogger()).to.not.throw();
            expect(mockOutputChannel.dispose.called).to.be.false;
            expect(getChannel()).to.be.null;
        });
    });
});
