/**
 * Unit tests for staticServer.ts
 *
 * Tests path traversal protection, MIME type mapping, index file serving,
 * and the /__vscode_storage__ endpoint. All I/O is mocked.
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import * as path from 'path';
import { EventEmitter } from 'events';

chai.use(chaiAsPromised);
const expect = chai.expect;

// ---------------------------------------------------------------------------
// Mock fs, http, and vscode before importing staticServer.
// ---------------------------------------------------------------------------

const fsStub = {
    existsSync: sinon.stub(),
    statSync: sinon.stub(),
    readFileSync: sinon.stub(),
    createReadStream: sinon.stub(),
};

// Fake HTTP server and request/response objects
class FakeServer extends EventEmitter {
    listen = sinon.stub().callsFake((port: number, host: string, cb: Function) => {
        // Immediately call the listen callback to resolve the promise
        process.nextTick(() => cb());
        return this;
    });
    close = sinon.stub().callsFake((cb?: Function) => {
        if (cb) process.nextTick(() => cb());
        return this;
    });
}

const fakeServer = new FakeServer();

const httpStub = {
    createServer: sinon.stub().returns(fakeServer),
    IncomingMessage: class extends EventEmitter {},
    ServerResponse: class extends EventEmitter {},
};

const Module = require('module');
const originalLoad = Module._load;

Module._load = function (request: string, parent: any, isMain: boolean) {
    if (request === 'fs') return fsStub;
    if (request === 'http') return httpStub;
    if (request === 'vscode') {
        // Return the vscode mock from test/vscodeMock
        return originalLoad.call(this, path.resolve(__dirname, 'vscodeMock.ts'), parent, isMain);
    }
    return originalLoad.call(this, request, parent, isMain);
};

import { createStaticServer } from '../staticServer';

Module._load = originalLoad;

// ---------------------------------------------------------------------------
// Helpers to create fake req/res
// ---------------------------------------------------------------------------

function createReq(url: string): any {
    const req = new EventEmitter();
    (req as any).url = url;
    return req;
}

function createRes(): any {
    const res = new EventEmitter() as any;
    res.writeHead = sinon.stub();
    res.end = sinon.stub();
    res.write = sinon.stub();
    return res;
}

function getRequestHandler(): Function {
    return httpStub.createServer.getCall(0).args[0];
}

// ---------------------------------------------------------------------------

describe('staticServer', () => {

    const wwwroot = path.sep === '\\' ? 'C:\\wwwroot' : '/wwwroot';
    const fakeContext: any = {
        globalState: {
            keys: sinon.stub().returns([]),
            get: sinon.stub().returns(undefined),
        },
    };

    afterEach(() => {
        sinon.restore();
        (fsStub.existsSync as sinon.SinonStub).reset();
        (fsStub.statSync as sinon.SinonStub).reset();
        (fsStub.readFileSync as sinon.SinonStub).reset();
        (fsStub.createReadStream as sinon.SinonStub).reset();
        httpStub.createServer.resetHistory();
        fakeServer.listen.resetHistory();
        fakeContext.globalState.keys.resetHistory();
        fakeContext.globalState.get.resetHistory();
    });

    // -----------------------------------------------------------------------
    // Server creation
    // -----------------------------------------------------------------------

    describe('createStaticServer', () => {
        it('should create and return an HTTP server', async () => {
            const server = await createStaticServer(wwwroot, fakeContext);
            expect(httpStub.createServer.calledOnce).to.be.true;
            expect(server).to.equal(fakeServer);
            expect(fakeServer.listen.calledOnce).to.be.true;
            // Should listen on port 0 (random) and 127.0.0.1
            expect(fakeServer.listen.getCall(0).args[0]).to.equal(0);
            expect(fakeServer.listen.getCall(0).args[1]).to.equal('127.0.0.1');
        });
    });

    // -----------------------------------------------------------------------
    // Path traversal protection
    // -----------------------------------------------------------------------

    describe('path traversal protection', () => {
        it('should reject ../ encoded paths with 403', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            const req = createReq('/../../../etc/passwd');
            const res = createRes();

            handler(req, res);

            expect(res.writeHead.calledOnce).to.be.true;
            expect(res.writeHead.getCall(0).args[0]).to.equal(403);
            expect(res.end.getCall(0).args[0]).to.include('Forbidden');
        });

        it('should reject URL-encoded ../ sequences', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            // %2F..%2F..%2F decodes to /../../
            const req = createReq('/%2F..%2F..%2Fetc/passwd');
            const res = createRes();

            handler(req, res);

            expect(res.writeHead.getCall(0).args[0]).to.equal(403);
        });

        it('should reject traversal via .. in subdirectory paths', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            const req = createReq('/subdir/../../secret.txt');
            const res = createRes();

            handler(req, res);

            expect(res.writeHead.getCall(0).args[0]).to.equal(403);
        });
    });

    // -----------------------------------------------------------------------
    // MIME type mapping
    // -----------------------------------------------------------------------

    describe('MIME type mapping', () => {
        async function testMime(ext: string, expectedMime: string) {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            const filePath = path.resolve(path.join(wwwroot, `file${ext}`));
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.statSync as sinon.SinonStub).returns({ isFile: () => true });
            (fsStub.createReadStream as sinon.SinonStub).returns({
                pipe: sinon.stub(),
            });

            const req = createReq(`/file${ext}`);
            const res = createRes();

            handler(req, res);

            const headers = res.writeHead.getCall(0).args[1] as any;
            expect(headers['Content-Type']).to.equal(expectedMime);
        }

        it('should serve .html as text/html', async () => {
            await testMime('.html', 'text/html');
        });

        it('should serve .js as application/javascript', async () => {
            await testMime('.js', 'application/javascript');
        });

        it('should serve .css as text/css', async () => {
            await testMime('.css', 'text/css');
        });

        it('should serve .json as application/json', async () => {
            await testMime('.json', 'application/json');
        });

        it('should serve .png as image/png', async () => {
            await testMime('.png', 'image/png');
        });

        it('should serve .wasm as application/wasm', async () => {
            await testMime('.wasm', 'application/wasm');
        });

        it('should serve unknown extensions as application/octet-stream', async () => {
            await testMime('.xyz', 'application/octet-stream');
        });
    });

    // -----------------------------------------------------------------------
    // Index file serving
    // -----------------------------------------------------------------------

    describe('index file serving', () => {
        it('should serve index.html when path is /', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            const indexPath = path.resolve(path.join(wwwroot, 'index.html'));
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.statSync as sinon.SinonStub).returns({ isFile: () => true });
            (fsStub.readFileSync as sinon.SinonStub).returns(
                '<html><head></head><body>Hello</body></html>'
            );

            const req = createReq('/');
            const res = createRes();

            handler(req, res);

            expect(res.writeHead.getCall(0).args[0]).to.equal(200);
            // Should inject storage script into index.html
            const responseBody = res.end.getCall(0).args[0] as string;
            expect(responseBody).to.include('__vscodeInitialStorage__');
            expect(responseBody).to.include('</head>');
        });

        it('should inject storage script before </head> in index.html', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.statSync as sinon.SinonStub).returns({ isFile: () => true });
            const html = '<html><head><title>Test</title></head><body></body></html>';
            (fsStub.readFileSync as sinon.SinonStub).returns(html);

            const req = createReq('/');
            const res = createRes();

            handler(req, res);

            const body = res.end.getCall(0).args[0] as string;
            // Script should be before </head>
            const scriptIdx = body.indexOf('__vscodeInitialStorage__');
            const headCloseIdx = body.indexOf('</head>');
            expect(scriptIdx).to.be.lessThan(headCloseIdx);
            expect(scriptIdx).to.be.greaterThan(-1);
        });

        it('should prepend script if no </head> tag found', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.statSync as sinon.SinonStub).returns({ isFile: () => true });
            (fsStub.readFileSync as sinon.SinonStub).returns('<html><body>No head</body></html>');

            const req = createReq('/');
            const res = createRes();

            handler(req, res);

            const body = res.end.getCall(0).args[0] as string;
            // Script should be at the beginning (prepended before the HTML)
            expect(body.indexOf('<script>')).to.equal(0);
            expect(body).to.include('__vscodeInitialStorage__');
            // The original HTML should come after the script
            const scriptEnd = body.indexOf('</script>') + '</script>'.length;
            expect(body.substring(scriptEnd)).to.include('<html>');
        });
    });

    // -----------------------------------------------------------------------
    // 404 handling
    // -----------------------------------------------------------------------

    describe('404 handling', () => {
        it('should return 404 for non-existent files', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const req = createReq('/nonexistent.html');
            const res = createRes();

            handler(req, res);

            expect(res.writeHead.getCall(0).args[0]).to.equal(404);
            expect(res.end.getCall(0).args[0]).to.include('Not Found');
        });

        it('should return 404 when path is a directory, not a file', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.statSync as sinon.SinonStub).returns({ isFile: () => false });

            const req = createReq('/somedir');
            const res = createRes();

            handler(req, res);

            expect(res.writeHead.getCall(0).args[0]).to.equal(404);
        });
    });

    // -----------------------------------------------------------------------
    // /__vscode_storage__ endpoint
    // -----------------------------------------------------------------------

    describe('/__vscode_storage__ endpoint', () => {
        it('should return globalState as JSON', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            fakeContext.globalState.keys.returns(['key1', 'key2']);
            fakeContext.globalState.get
                .onCall(0).returns('value1')
                .onCall(1).returns('value2');

            const req = createReq('/__vscode_storage__');
            const res = createRes();

            handler(req, res);

            expect(res.writeHead.getCall(0).args[0]).to.equal(200);
            const headers = res.writeHead.getCall(0).args[1] as any;
            expect(headers['Content-Type']).to.equal('application/json');

            const body = JSON.parse(res.end.getCall(0).args[0] as string);
            expect(body).to.deep.equal({ key1: 'value1', key2: 'value2' });
        });

        it('should only include string values from globalState', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            fakeContext.globalState.keys.returns(['strKey', 'numKey']);
            fakeContext.globalState.get
                .onCall(0).returns('a string')
                .onCall(1).returns(42); // non-string, should be excluded

            const req = createReq('/__vscode_storage__');
            const res = createRes();

            handler(req, res);

            const body = JSON.parse(res.end.getCall(0).args[0] as string);
            expect(body).to.deep.equal({ strKey: 'a string' });
        });
    });

    // -----------------------------------------------------------------------
    // Query string stripping
    // -----------------------------------------------------------------------

    describe('query string handling', () => {
        it('should strip query parameters from URL', async () => {
            await createStaticServer(wwwroot, fakeContext);
            const handler = getRequestHandler();

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.statSync as sinon.SinonStub).returns({ isFile: () => true });
            (fsStub.createReadStream as sinon.SinonStub).returns({ pipe: sinon.stub() });

            const req = createReq('/app.js?v=12345');
            const res = createRes();

            handler(req, res);

            // Should serve the file (200), ignoring ?v=12345
            expect(res.writeHead.getCall(0).args[0]).to.equal(200);
        });
    });
});
