/**
 * Unit tests for networkProxy.ts
 *
 * Tests setSkipSslValidation, httpRequest (via handleNetworkProxyRequest),
 * and error handling. All HTTP/HTTPS module calls are mocked.
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import { EventEmitter } from 'events';
import * as path from 'path';

chai.use(chaiAsPromised);
const expect = chai.expect;

// ---------------------------------------------------------------------------
// Mock https, http, and vscode before importing networkProxy.
// ---------------------------------------------------------------------------

class FakeClientRequest extends EventEmitter {
    write = sinon.stub();
    end = sinon.stub().callsFake(() => {
        // Simulate the request being sent; the handler will emit response/error
        this._doResponse();
    });
    _doResponse() { /* overridden per test */ }
    setTimeout = sinon.stub();
    abort = sinon.stub();
}

class FakeIncomingMessage extends EventEmitter {
    statusCode: number = 200;
    statusMessage: string = 'OK';
    headers: Record<string, string> = {};
    constructor() {
        super();
    }

    // Make it async-iterable so `for await (const data of response)` works
    // in the streaming path of handleNetworkProxyRequest.
    async *[Symbol.asyncIterator](): AsyncIterator<Buffer> {
        const queue: Buffer[] = [];
        let done = false;
        let resolveWait: (() => void) | null = null;

        const onData = (chunk: Buffer) => {
            queue.push(chunk);
            if (resolveWait) {
                resolveWait();
                resolveWait = null;
            }
        };
        const onEnd = () => {
            done = true;
            if (resolveWait) {
                resolveWait();
                resolveWait = null;
            }
        };
        const onError = (err: Error) => {
            done = true;
            if (resolveWait) {
                resolveWait();
                resolveWait = null;
            }
            throw err;
        };

        this.on('data', onData);
        this.on('end', onEnd);
        this.on('error', onError);

        try {
            while (!done) {
                if (queue.length > 0) {
                    yield queue.shift()!;
                } else {
                    await new Promise<void>(r => { resolveWait = r; });
                }
            }
            // Drain remaining
            while (queue.length > 0) {
                yield queue.shift()!;
            }
        } finally {
            this.off('data', onData);
            this.off('end', onEnd);
            this.off('error', onError);
        }
    }
}

const httpsStub = {
    request: sinon.stub(),
    Agent: sinon.stub().returns({}),
};

const httpStub = {
    request: sinon.stub(),
};

const Module = require('module');
const originalLoad = Module._load;

Module._load = function (request: string, parent: any, isMain: boolean) {
    if (request === 'https') return httpsStub;
    if (request === 'http') return httpStub;
    if (request === 'vscode') {
        return originalLoad.call(this, path.resolve(__dirname, 'vscodeMock.ts'), parent, isMain);
    }
    return originalLoad.call(this, request, parent, isMain);
};

import { setSkipSslValidation, handleNetworkProxyRequest } from '../networkProxy';

Module._load = originalLoad;

// ---------------------------------------------------------------------------
// Helper: set up a mock HTTP/HTTPS request that calls the callback with the
// response, then emits data/end events on a later event-loop turn (using
// setImmediate so the events fire AFTER promise microtasks that register
// listeners).
// ---------------------------------------------------------------------------

function setupMockResponse(
    stub: sinon.SinonStub,
    fakeReq: FakeClientRequest,
    fakeRes: FakeIncomingMessage,
    chunks: Buffer[],
    emitOnEnd: boolean = true
) {
    stub.callsFake((options: any, cb: Function) => {
        // Call the callback on nextTick (resolves the httpRequest promise)
        process.nextTick(() => cb(fakeRes));
        // Emit data/end on setImmediate (runs after promise microtasks,
        // so listeners registered by readFullBody / for-await are in place)
        setImmediate(() => {
            for (const chunk of chunks) {
                fakeRes.emit('data', chunk);
            }
            if (emitOnEnd) {
                fakeRes.emit('end');
            }
        });
        return fakeReq;
    });
}

function setupMockError(
    stub: sinon.SinonStub,
    fakeReq: FakeClientRequest,
    err: Error
) {
    stub.callsFake((options: any, cb: Function) => {
        setImmediate(() => {
            fakeReq.emit('error', err);
        });
        return fakeReq;
    });
}

// ---------------------------------------------------------------------------

describe('networkProxy', () => {

    afterEach(() => {
        sinon.restore();
        httpsStub.request.resetHistory();
        httpStub.request.resetHistory();
        httpsStub.Agent.resetHistory();
        // Reset SSL to default
        setSkipSslValidation(false);
    });

    // -----------------------------------------------------------------------
    // setSkipSslValidation
    // -----------------------------------------------------------------------

    describe('setSkipSslValidation', () => {
        it('should set NODE_TLS_REJECT_UNAUTHORIZED to "0" when enabled', () => {
            setSkipSslValidation(true);
            expect(process.env.NODE_TLS_REJECT_UNAUTHORIZED).to.equal('0');
        });

        it('should set NODE_TLS_REJECT_UNAUTHORIZED to "1" when disabled', () => {
            setSkipSslValidation(false);
            expect(process.env.NODE_TLS_REJECT_UNAUTHORIZED).to.equal('1');
        });

        it('should toggle correctly when called multiple times', () => {
            setSkipSslValidation(true);
            expect(process.env.NODE_TLS_REJECT_UNAUTHORIZED).to.equal('0');
            setSkipSslValidation(false);
            expect(process.env.NODE_TLS_REJECT_UNAUTHORIZED).to.equal('1');
            setSkipSslValidation(true);
            expect(process.env.NODE_TLS_REJECT_UNAUTHORIZED).to.equal('0');
        });
    });

    // -----------------------------------------------------------------------
    // handleNetworkProxyRequest — non-streaming
    // -----------------------------------------------------------------------

    describe('handleNetworkProxyRequest (non-streaming)', () => {
        it('should send headers, body chunk, and end for successful non-streaming response', async () => {
            const fakeReq = new FakeClientRequest();
            const fakeRes = new FakeIncomingMessage();
            fakeRes.statusCode = 200;
            fakeRes.statusMessage = 'OK';
            fakeRes.headers = { 'content-type': 'application/json' };

            setupMockResponse(httpStub.request, fakeReq, fakeRes, [
                Buffer.from('{"result":"success"}'),
            ]);

            const postMessageStub = sinon.stub();
            const fakePanel: any = {
                webview: { postMessage: postMessageStub },
            };

            await handleNetworkProxyRequest(fakePanel, {
                requestId: 'req-1',
                url: 'http://localhost:3000/api',
                method: 'GET',
                headers: {},
            });

            // Should send 3 messages: headers, chunk, end
            expect(postMessageStub.callCount).to.equal(3);

            const headersMsg = postMessageStub.getCall(0).args[0];
            expect(headersMsg.type).to.equal('VsStreamingPacket');
            expect(headersMsg.payload.type).to.equal('headers');
            expect(headersMsg.payload.requestId).to.equal('req-1');
            expect(headersMsg.payload.statusCode).to.equal(200);

            const chunkMsg = postMessageStub.getCall(1).args[0];
            expect(chunkMsg.payload.type).to.equal('chunk');
            expect(chunkMsg.payload.chunk).to.include('success');

            const endMsg = postMessageStub.getCall(2).args[0];
            expect(endMsg.payload.type).to.equal('end');
            expect(endMsg.payload.success).to.be.true;
        });

        it('should send headers and end without chunk for empty body', async () => {
            const fakeReq = new FakeClientRequest();
            const fakeRes = new FakeIncomingMessage();
            fakeRes.statusCode = 204;
            fakeRes.statusMessage = 'No Content';
            fakeRes.headers = { 'content-type': 'text/plain' };

            setupMockResponse(httpStub.request, fakeReq, fakeRes, []);

            const postMessageStub = sinon.stub();
            const fakePanel: any = {
                webview: { postMessage: postMessageStub },
            };

            await handleNetworkProxyRequest(fakePanel, {
                requestId: 'req-2',
                url: 'http://localhost:3000/empty',
                method: 'GET',
                headers: {},
            });

            // Only headers + end (no chunk since body is empty)
            expect(postMessageStub.callCount).to.equal(2);
            expect(postMessageStub.getCall(0).args[0].payload.type).to.equal('headers');
            expect(postMessageStub.getCall(1).args[0].payload.type).to.equal('end');
        });
    });

    // -----------------------------------------------------------------------
    // handleNetworkProxyRequest — streaming (SSE)
    // -----------------------------------------------------------------------

    describe('handleNetworkProxyRequest (streaming SSE)', () => {
        it('should forward chunks as they arrive for text/event-stream', async () => {
            const fakeReq = new FakeClientRequest();
            const fakeRes = new FakeIncomingMessage();
            fakeRes.statusCode = 200;
            fakeRes.statusMessage = 'OK';
            fakeRes.headers = { 'content-type': 'text/event-stream' };

            setupMockResponse(httpStub.request, fakeReq, fakeRes, [
                Buffer.from('data: chunk1\n\n'),
                Buffer.from('data: chunk2\n\n'),
            ]);

            const postMessageStub = sinon.stub();
            const fakePanel: any = {
                webview: { postMessage: postMessageStub },
            };

            await handleNetworkProxyRequest(fakePanel, {
                requestId: 'req-sse',
                url: 'http://localhost:3000/stream',
                method: 'GET',
                headers: {},
            });

            // headers + 2 chunks + end = 4 messages
            expect(postMessageStub.callCount).to.equal(4);
            expect(postMessageStub.getCall(0).args[0].payload.type).to.equal('headers');
            expect(postMessageStub.getCall(1).args[0].payload.type).to.equal('chunk');
            expect(postMessageStub.getCall(1).args[0].payload.chunk).to.include('chunk1');
            expect(postMessageStub.getCall(2).args[0].payload.chunk).to.include('chunk2');
            expect(postMessageStub.getCall(3).args[0].payload.type).to.equal('end');
            expect(postMessageStub.getCall(3).args[0].payload.success).to.be.true;
        });
    });

    // -----------------------------------------------------------------------
    // handleNetworkProxyRequest — error handling
    // -----------------------------------------------------------------------

    describe('handleNetworkProxyRequest (error handling)', () => {
        it('should send error end message on request failure', async () => {
            const fakeReq = new FakeClientRequest();

            const err = new Error('Connection refused');
            (err as any).code = 'ECONNREFUSED';
            setupMockError(httpStub.request, fakeReq, err);

            const postMessageStub = sinon.stub();
            const fakePanel: any = {
                webview: { postMessage: postMessageStub },
            };

            await handleNetworkProxyRequest(fakePanel, {
                requestId: 'req-err',
                url: 'http://localhost:3000/fail',
                method: 'GET',
                headers: {},
            });

            // Should send just an error end message
            expect(postMessageStub.callCount).to.equal(1);
            const msg = postMessageStub.getCall(0).args[0];
            expect(msg.payload.type).to.equal('end');
            expect(msg.payload.success).to.be.false;
            expect(msg.payload.error).to.include('Connection refused');
            expect(msg.payload.error).to.include('ECONNREFUSED');
        });

        it('should include SSL hint for certificate errors', async () => {
            const fakeReq = new FakeClientRequest();

            const err = new Error('Self signed cert');
            (err as any).code = 'DEPTH_ZERO_SELF_SIGNED_CERT';
            setupMockError(httpsStub.request, fakeReq, err);

            const postMessageStub = sinon.stub();
            const fakePanel: any = {
                webview: { postMessage: postMessageStub },
            };

            await handleNetworkProxyRequest(fakePanel, {
                requestId: 'req-ssl',
                url: 'https://localhost:8443/api',
                method: 'GET',
                headers: {},
            });

            const msg = postMessageStub.getCall(0).args[0];
            expect(msg.payload.error).to.include('Skip SSL');
        });
    });

    // -----------------------------------------------------------------------
    // httpRequest — HTTPS with SSL skip
    // -----------------------------------------------------------------------

    describe('httpRequest with SSL skip', () => {
        it('should use custom agent when skipSslValidation is enabled for HTTPS', async () => {
            setSkipSslValidation(true);

            const fakeReq = new FakeClientRequest();
            const fakeRes = new FakeIncomingMessage();
            fakeRes.headers = { 'content-type': 'text/plain' };

            httpsStub.request.callsFake((options: any, cb: Function) => {
                // Verify agent is set
                expect(options.agent).to.exist;
                process.nextTick(() => cb(fakeRes));
                setImmediate(() => fakeRes.emit('end'));
                return fakeReq;
            });

            const postMessageStub = sinon.stub();
            const fakePanel: any = {
                webview: { postMessage: postMessageStub },
            };

            await handleNetworkProxyRequest(fakePanel, {
                requestId: 'req-ssl-skip',
                url: 'https://localhost:8443/secure',
                method: 'GET',
                headers: {},
            });

            // Agent should have been created
            expect(httpsStub.Agent.calledOnce).to.be.true;
            const agentArgs = httpsStub.Agent.getCall(0).args[0];
            expect(agentArgs.rejectUnauthorized).to.be.false;
        });

        it('should not create custom agent for HTTP requests', async () => {
            setSkipSslValidation(true);

            const fakeReq = new FakeClientRequest();
            const fakeRes = new FakeIncomingMessage();
            fakeRes.headers = { 'content-type': 'text/plain' };

            httpStub.request.callsFake((options: any, cb: Function) => {
                expect(options.agent).to.be.undefined;
                process.nextTick(() => cb(fakeRes));
                setImmediate(() => fakeRes.emit('end'));
                return fakeReq;
            });

            const postMessageStub = sinon.stub();
            const fakePanel: any = {
                webview: { postMessage: postMessageStub },
            };

            await handleNetworkProxyRequest(fakePanel, {
                requestId: 'req-http',
                url: 'http://localhost:3000/api',
                method: 'GET',
                headers: {},
            });

            // Agent should NOT have been created for HTTP
            expect(httpsStub.Agent.called).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // httpRequest — POST with body
    // -----------------------------------------------------------------------

    describe('httpRequest with body', () => {
        it('should write body for POST requests', async () => {
            const fakeReq = new FakeClientRequest();
            const fakeRes = new FakeIncomingMessage();
            fakeRes.headers = { 'content-type': 'application/json' };

            httpStub.request.callsFake((options: any, cb: Function) => {
                expect(options.method).to.equal('POST');
                process.nextTick(() => cb(fakeRes));
                setImmediate(() => {
                    fakeRes.emit('data', Buffer.from('{"ok":true}'));
                    fakeRes.emit('end');
                });
                return fakeReq;
            });

            const postMessageStub = sinon.stub();
            const fakePanel: any = {
                webview: { postMessage: postMessageStub },
            };

            await handleNetworkProxyRequest(fakePanel, {
                requestId: 'req-post',
                url: 'http://localhost:3000/api',
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: '{"query":"test"}',
            });

            // write should have been called with the body
            expect(fakeReq.write.calledOnce).to.be.true;
            expect(fakeReq.write.getCall(0).args[0]).to.equal('{"query":"test"}');
        });

        it('should NOT write body for GET requests even if body is provided', async () => {
            const fakeReq = new FakeClientRequest();
            const fakeRes = new FakeIncomingMessage();
            fakeRes.headers = { 'content-type': 'text/plain' };

            setupMockResponse(httpStub.request, fakeReq, fakeRes, []);

            const postMessageStub = sinon.stub();
            const fakePanel: any = {
                webview: { postMessage: postMessageStub },
            };

            await handleNetworkProxyRequest(fakePanel, {
                requestId: 'req-get-body',
                url: 'http://localhost:3000/api',
                method: 'GET',
                headers: {},
                body: 'should not be sent',
            });

            expect(fakeReq.write.called).to.be.false;
        });
    });
});
