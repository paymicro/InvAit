/**
 * Unit tests for webviewMessageHandler.ts
 *
 * Tests the shared webview message handler by mocking toolDispatcher,
 * networkProxy, and logger modules via Module._load interception.
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import * as path from 'path';

chai.use(chaiAsPromised);
const expect = chai.expect;

// ---------------------------------------------------------------------------
// Mock the dependencies BEFORE importing webviewMessageHandler.
// We intercept Module._load and return mock objects with sinon stubs for
// toolDispatcher, networkProxy, and logger.
// ---------------------------------------------------------------------------

const toolDispatcherStub = {
    handleVsRequest: sinon.stub().resolves(),
    setOnUiReady: sinon.stub(),
    disposeMcpRegistry: sinon.stub().resolves(),
};

const networkProxyStub = {
    handleNetworkProxyRequest: sinon.stub().resolves(),
    setSkipSslValidation: sinon.stub(),
    setExtensionUserAgent: sinon.stub(),
};

const loggerStub = {
    log: sinon.stub(),
    logError: sinon.stub(),
    logWarning: sinon.stub(),
    disposeLogger: sinon.stub(),
    initLogger: sinon.stub(),
    getChannel: sinon.stub().returns(null),
    showOutput: sinon.stub(),
};

const Module = require('module');
const originalLoad = Module._load;

Module._load = function (request: string, parent: any, isMain: boolean) {
    if (request === 'vscode') {
        return originalLoad.call(this, path.resolve(__dirname, 'vscodeMock.ts'), parent, isMain);
    }
    if (request.includes('toolDispatcher')) return toolDispatcherStub;
    if (request.includes('networkProxy')) return networkProxyStub;
    if (request.includes('logger') && !request.includes('vscodeMock')) return loggerStub;
    return originalLoad.call(this, request, parent, isMain);
};

import { handleWebviewMessage, WebviewMessageContext } from '../webviewMessageHandler';

Module._load = originalLoad;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function createContext(): WebviewMessageContext {
    const postMessageStub = sinon.stub();
    const globalStateUpdateStub = sinon.stub().resolves();
    const fakePanel: any = {
        webview: { postMessage: postMessageStub },
    };
    const fakeContext: any = {
        globalState: { update: globalStateUpdateStub },
    };
    return { panel: fakePanel, context: fakeContext } as unknown as WebviewMessageContext;
}

// ---------------------------------------------------------------------------

describe('webviewMessageHandler', () => {

    afterEach(() => {
        sinon.restore();
        toolDispatcherStub.handleVsRequest.resetHistory();
        networkProxyStub.handleNetworkProxyRequest.resetHistory();
        networkProxyStub.setSkipSslValidation.resetHistory();
        loggerStub.log.resetHistory();
    });

    // -----------------------------------------------------------------------
    // NetworkProxyRequest
    // -----------------------------------------------------------------------

    describe('NetworkProxyRequest', () => {
        it('should delegate to handleNetworkProxyRequest with panel and payload', async () => {
            const ctx = createContext();
            const payload = { requestId: 'req-1', url: 'http://localhost:3000/api', method: 'GET', headers: {} };
            const message = { command: 'NetworkProxyRequest', payload };

            await handleWebviewMessage(message, ctx);

            expect(networkProxyStub.handleNetworkProxyRequest.calledOnce).to.be.true;
            const callArgs = networkProxyStub.handleNetworkProxyRequest.getCall(0).args;
            expect(callArgs[0]).to.equal(ctx.panel);
            expect(callArgs[1]).to.deep.equal(payload);
        });
    });

    // -----------------------------------------------------------------------
    // StorageSet
    // -----------------------------------------------------------------------

    describe('StorageSet', () => {
        it('should call globalState.update with key and value', async () => {
            const ctx = createContext();
            const payload = { key: 'myKey', value: 'myValue' };
            const message = { command: 'StorageSet', payload };

            await handleWebviewMessage(message, ctx);

            const updateStub = (ctx.context as any).globalState.update as sinon.SinonStub;
            expect(updateStub.calledOnce).to.be.true;
            expect(updateStub.getCall(0).args[0]).to.equal('myKey');
            expect(updateStub.getCall(0).args[1]).to.equal('myValue');
        });
    });

    // -----------------------------------------------------------------------
    // StorageRemove
    // -----------------------------------------------------------------------

    describe('StorageRemove', () => {
        it('should call globalState.update with key and undefined', async () => {
            const ctx = createContext();
            const payload = { key: 'removeKey' };
            const message = { command: 'StorageRemove', payload };

            await handleWebviewMessage(message, ctx);

            const updateStub = (ctx.context as any).globalState.update as sinon.SinonStub;
            expect(updateStub.calledOnce).to.be.true;
            expect(updateStub.getCall(0).args[0]).to.equal('removeKey');
            expect(updateStub.getCall(0).args[1]).to.be.undefined;
        });
    });

    // -----------------------------------------------------------------------
    // skip_ssl_validation
    // -----------------------------------------------------------------------

    describe('skip_ssl_validation', () => {
        it('should call setSkipSslValidation(true) when payload is "True"', async () => {
            const ctx = createContext();
            const message = { action: 'skip_ssl_validation', payload: 'True' };

            await handleWebviewMessage(message, ctx);

            expect(networkProxyStub.setSkipSslValidation.calledOnce).to.be.true;
            expect(networkProxyStub.setSkipSslValidation.getCall(0).args[0]).to.be.true;
        });

        it('should call setSkipSslValidation(true) when payload is "true"', async () => {
            const ctx = createContext();
            const message = { action: 'skip_ssl_validation', payload: 'true' };

            await handleWebviewMessage(message, ctx);

            expect(networkProxyStub.setSkipSslValidation.calledOnce).to.be.true;
            expect(networkProxyStub.setSkipSslValidation.getCall(0).args[0]).to.be.true;
        });

        it('should call setSkipSslValidation(false) when payload is "false"', async () => {
            const ctx = createContext();
            const message = { action: 'skip_ssl_validation', payload: 'false' };

            await handleWebviewMessage(message, ctx);

            expect(networkProxyStub.setSkipSslValidation.calledOnce).to.be.true;
            expect(networkProxyStub.setSkipSslValidation.getCall(0).args[0]).to.be.false;
        });

        it('should call setSkipSslValidation(false) when payload is "False" (not "True" or "true")', async () => {
            const ctx = createContext();
            const message = { action: 'skip_ssl_validation', payload: 'False' };

            await handleWebviewMessage(message, ctx);

            expect(networkProxyStub.setSkipSslValidation.calledOnce).to.be.true;
            expect(networkProxyStub.setSkipSslValidation.getCall(0).args[0]).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // VsRequest (action + correlationId)
    // -----------------------------------------------------------------------

    describe('VsRequest', () => {
        it('should delegate to handleVsRequest when action and correlationId are present', async () => {
            const ctx = createContext();
            const message = { action: 'read_files', correlationId: 'corr-123', payload: '{}' };

            await handleWebviewMessage(message, ctx);

            expect(toolDispatcherStub.handleVsRequest.calledOnce).to.be.true;
            const callArgs = toolDispatcherStub.handleVsRequest.getCall(0).args;
            expect(callArgs[0]).to.equal(ctx.panel);
            expect(callArgs[1]).to.deep.equal(message);
        });
    });

    // -----------------------------------------------------------------------
    // Unknown / unhandled messages
    // -----------------------------------------------------------------------

    describe('Unknown message', () => {
        it('should call log with "Unhandled message" for unrecognized messages', async () => {
            const ctx = createContext();
            const message = { foo: 'bar' };

            await handleWebviewMessage(message, ctx);

            expect(loggerStub.log.calledOnce).to.be.true;
            const loggedArg = loggerStub.log.getCall(0).args[0] as string;
            expect(loggedArg).to.include('[webview] Unhandled message:');
            expect(loggedArg).to.include('foo');
        });

        it('should treat message with action but no correlationId as unhandled', async () => {
            const ctx = createContext();
            const message = { action: 'some_action' };

            await handleWebviewMessage(message, ctx);

            expect(toolDispatcherStub.handleVsRequest.called).to.be.false;
            expect(loggerStub.log.calledOnce).to.be.true;
            const loggedArg = loggerStub.log.getCall(0).args[0] as string;
            expect(loggedArg).to.include('[webview] Unhandled message:');
        });
    });
});
