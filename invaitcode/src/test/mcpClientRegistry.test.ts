/**
 * Unit tests for mcpClientRegistry.ts
 *
 * Tests the Semaphore class, McpClientRegistry.resolveCommand(),
 * McpClientRegistry.buildFingerprint(), and config-related logic.
 * All external dependencies (fs, child_process, MCP SDK) are mocked.
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import * as path from 'path';

chai.use(chaiAsPromised);
const expect = chai.expect;

// ---------------------------------------------------------------------------
// Mock the MCP SDK modules before importing mcpClientRegistry.
// ---------------------------------------------------------------------------

const mockClientInstance = {
    connect: sinon.stub(),
    close: sinon.stub(),
    listTools: sinon.stub(),
    callTool: sinon.stub(),
};

const MockClient = sinon.stub().returns(mockClientInstance);

const MockStdioTransport = sinon.stub().returns({});
const MockStreamableHTTPTransport = sinon.stub().returns({});

const Module = require('module');
const originalLoad = Module._load;

Module._load = function (request: string, parent: any, isMain: boolean) {
    // Intercept MCP SDK imports
    if (request === '@modelcontextprotocol/sdk/client/index.js' ||
        request === '@modelcontextprotocol/sdk/client/index') {
        return { Client: MockClient };
    }
    if (request === '@modelcontextprotocol/sdk/client/stdio.js') {
        return { StdioClientTransport: MockStdioTransport };
    }
    if (request === '@modelcontextprotocol/sdk/client/streamableHttp.js') {
        return { StreamableHTTPClientTransport: MockStreamableHTTPTransport };
    }
    return originalLoad.call(this, request, parent, isMain);
};

// Now import — the module will receive our mocks.
import { McpClientRegistry, McpServerLaunchInfo } from '../mcpClientRegistry';

// Restore
Module._load = originalLoad;

// ---------------------------------------------------------------------------

describe('mcpClientRegistry', () => {

    let resolveCommandStub: sinon.SinonStub | null = null;

    beforeEach(() => {
        // Stub resolveCommand for all tests except the dedicated resolveCommand suite.
        // Tests that need real resolveCommand behavior restore the stub in their own beforeEach.
        resolveCommandStub = sinon.stub(McpClientRegistry, 'resolveCommand').returns('/usr/bin/echo');
    });

    afterEach(() => {
        sinon.restore();
        // Restore resolveCommand if it was stubbed
        if (resolveCommandStub) {
            resolveCommandStub.restore();
            resolveCommandStub = null;
        }
        MockClient.resetHistory();
        MockStdioTransport.resetHistory();
        MockStreamableHTTPTransport.resetHistory();
        mockClientInstance.connect.resetHistory();
        mockClientInstance.close.resetHistory();
        mockClientInstance.listTools.resetHistory();
        mockClientInstance.callTool.resetHistory();
    });

    // -----------------------------------------------------------------------
    // Semaphore (tested indirectly via getOrStart serialization, but we can
    // also test the class directly by accessing it through the registry's
    // internal lock behavior)
    // -----------------------------------------------------------------------

    describe('Semaphore (via registry concurrency)', () => {
        it('should serialize concurrent getOrStart calls for the same serverId', async () => {
            const registry = new McpClientRegistry(() => {});

            // Make connect slow to simulate startup
            let connectCallCount = 0;
            mockClientInstance.connect.callsFake(async () => {
                connectCallCount++;
                await new Promise(r => setTimeout(r, 50));
            });

            const params: McpServerLaunchInfo = {
                serverId: 'test-server',
                command: 'echo',
            };

            // Launch two concurrent listTools calls — both need getOrStart
            mockClientInstance.listTools.resolves({ tools: [] });

            const [r1, r2] = await Promise.all([
                registry.listTools(params),
                registry.listTools(params),
            ]);

            // Both should succeed
            expect((r1 as any).tools).to.deep.equal([]);
            expect((r2 as any).tools).to.deep.equal([]);

            // connect should only be called once (second call reuses cached client)
            expect(connectCallCount).to.equal(1);

            await registry.dispose();
        });

        it('should allow concurrent operations on different serverIds', async () => {
            const registry = new McpClientRegistry(() => {});

            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            const params1: McpServerLaunchInfo = {
                serverId: 'server-a',
                command: 'echo',
            };
            const params2: McpServerLaunchInfo = {
                serverId: 'server-b',
                command: 'cat',
            };

            await Promise.all([
                registry.listTools(params1),
                registry.listTools(params2),
            ]);

            // Two different servers → two Client instances created
            expect(MockClient.callCount).to.equal(2);

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // resolveCommand
    // -----------------------------------------------------------------------

    describe('resolveCommand', () => {
        let fsExistsStub: sinon.SinonStub;
        let origPath: string | undefined;
        let origPathext: string | undefined;
        let origPlatform: string;

        beforeEach(() => {
            // Restore the global resolveCommand stub so we test the real implementation.
            resolveCommandStub!.restore();
            resolveCommandStub = null;

            // Save env
            origPath = process.env.PATH;
            origPathext = process.env.PATHEXT;
            origPlatform = process.platform;

            // Stub fs.existsSync for resolveCommand
            const fs = require('fs');
            fsExistsStub = sinon.stub(fs, 'existsSync');
        });

        afterEach(() => {
            fsExistsStub.restore();
            process.env.PATH = origPath;
            process.env.PATHEXT = origPathext;
            // Restore process.platform via defineProperty (it's read-only in newer Node)
            Object.defineProperty(process, 'platform', {
                value: origPlatform,
                writable: true,
                configurable: true,
            });
        });

        it('should return null for empty command', () => {
            expect(McpClientRegistry.resolveCommand('')).to.be.null;
            expect(McpClientRegistry.resolveCommand('   ')).to.be.null;
        });

        it('should resolve absolute path when file exists', () => {
            const absPath = path.sep === '\\' ? 'C:\\tools\\mycmd.exe' : '/usr/bin/mycmd';
            fsExistsStub.returns(true);

            const result = McpClientRegistry.resolveCommand(absPath);
            expect(result).to.not.be.null;
            expect(result).to.equal(absPath);
        });

        it('should return null for absolute path when file does not exist', () => {
            const absPath = path.sep === '\\' ? 'C:\\nonexistent\\cmd.exe' : '/nonexistent/cmd';
            fsExistsStub.returns(false);

            expect(McpClientRegistry.resolveCommand(absPath)).to.be.null;
        });

        it('should resolve relative path with separator when file exists', () => {
            const relPath = path.join('.', 'local', 'tool');
            fsExistsStub.returns(true);

            const result = McpClientRegistry.resolveCommand(relPath);
            expect(result).to.not.be.null;
            expect(result).to.equal(path.resolve(relPath));
        });

        it('should search PATH directories and find the command', () => {
            // Simulate non-Windows for simplicity
            Object.defineProperty(process, 'platform', { value: 'linux', configurable: true });
            process.env.PATH = '/usr/local/bin:/usr/bin:/bin';
            process.env.PATHEXT = '';

            // existsSync returns false for first dir, true for second
            fsExistsStub.callsFake((p: string) => p === path.join('/usr/bin', 'mytool'));

            const result = McpClientRegistry.resolveCommand('mytool');
            expect(result).to.not.be.null;
            expect(result).to.equal(path.join('/usr/bin', 'mytool'));
        });

        it('should return null when command not found in PATH', () => {
            Object.defineProperty(process, 'platform', { value: 'linux', configurable: true });
            process.env.PATH = '/usr/bin:/bin';
            process.env.PATHEXT = '';
            fsExistsStub.returns(false);

            expect(McpClientRegistry.resolveCommand('nonexistent-tool')).to.be.null;
        });

        it('should try PATHEXT extensions on Windows', () => {
            Object.defineProperty(process, 'platform', { value: 'win32', configurable: true });
            process.env.PATH = 'C:\\node';
            process.env.PATHEXT = '.COM;.EXE;.BAT;.CMD';

            // Simulate that npx.cmd exists
            fsExistsStub.callsFake((p: string) =>
                p === path.join('C:\\node', 'npx.CMD') || p === path.join('C:\\node', 'npx.cmd')
            );

            const result = McpClientRegistry.resolveCommand('npx');
            expect(result).to.not.be.null;
        });
    });

    // -----------------------------------------------------------------------
    // buildFingerprint
    // -----------------------------------------------------------------------

    describe('buildFingerprint', () => {
        it('should produce consistent fingerprint for same params', () => {
            const params: McpServerLaunchInfo = {
                serverId: 'srv',
                command: 'npx',
                args: ['-y', '@modelcontextprotocol/server'],
            };
            const fp1 = McpClientRegistry.buildFingerprint(params);
            const fp2 = McpClientRegistry.buildFingerprint(params);
            expect(fp1).to.equal(fp2);
        });

        it('should differ when command changes', () => {
            const base: McpServerLaunchInfo = {
                serverId: 'srv',
                command: 'npx',
            };
            const modified = { ...base, command: 'node' };
            expect(McpClientRegistry.buildFingerprint(base))
                .to.not.equal(McpClientRegistry.buildFingerprint(modified));
        });

        it('should differ when args change', () => {
            const base: McpServerLaunchInfo = {
                serverId: 'srv',
                command: 'npx',
                args: ['arg1'],
            };
            const modified = { ...base, args: ['arg2'] };
            expect(McpClientRegistry.buildFingerprint(base))
                .to.not.equal(McpClientRegistry.buildFingerprint(modified));
        });

        it('should differ when url changes', () => {
            const base: McpServerLaunchInfo = {
                serverId: 'srv',
                command: '',
                url: 'http://localhost:3000',
            };
            const modified = { ...base, url: 'http://localhost:3001' };
            expect(McpClientRegistry.buildFingerprint(base))
                .to.not.equal(McpClientRegistry.buildFingerprint(modified));
        });

        it('should include env vars in fingerprint (sorted)', () => {
            const params1: McpServerLaunchInfo = {
                serverId: 'srv',
                command: 'node',
                env: { B: '2', A: '1' },
            };
            const params2: McpServerLaunchInfo = {
                serverId: 'srv',
                command: 'node',
                env: { A: '1', B: '2' },
            };
            // Same env content, different key order → same fingerprint
            expect(McpClientRegistry.buildFingerprint(params1))
                .to.equal(McpClientRegistry.buildFingerprint(params2));
        });

        it('should include headers in fingerprint (sorted, lowercased keys) for HTTP servers', () => {
            const params: McpServerLaunchInfo = {
                serverId: 'srv',
                command: '',
                url: 'http://localhost:3000',
                headers: { Authorization: 'Bearer token' },
            };
            const fp = McpClientRegistry.buildFingerprint(params);
            expect(fp).to.include('authorization=Bearer token');
        });

        it('should not include headers for stdio servers', () => {
            const params: McpServerLaunchInfo = {
                serverId: 'srv',
                command: 'node',
                headers: { Authorization: 'Bearer token' },
            };
            const fp = McpClientRegistry.buildFingerprint(params);
            expect(fp).to.not.include('authorization');
        });

        it('should include workingDirectory in fingerprint', () => {
            const base: McpServerLaunchInfo = {
                serverId: 'srv',
                command: 'node',
            };
            const modified = { ...base, workingDirectory: '/home/user' };
            expect(McpClientRegistry.buildFingerprint(base))
                .to.not.equal(McpClientRegistry.buildFingerprint(modified));
        });
    });

    // -----------------------------------------------------------------------
    // Registry lifecycle: stopServer, stopAll, describeServers
    // -----------------------------------------------------------------------

    describe('stopServer / stopAll / describeServers', () => {
        it('should return false when stopping a non-running server', async () => {
            const registry = new McpClientRegistry(() => {});
            const result = await registry.stopServer('nonexistent');
            expect(result).to.be.false;
            await registry.dispose();
        });

        it('should stop a running server and return true', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });
            mockClientInstance.close.resolves(undefined);

            const params: McpServerLaunchInfo = {
                serverId: 'srv-stop',
                command: 'echo',
            };
            await registry.listTools(params);

            const stopped = await registry.stopServer('srv-stop');
            expect(stopped).to.be.true;
            expect(mockClientInstance.close.calledOnce).to.be.true;

            await registry.dispose();
        });

        it('should stop all servers and return count', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });
            mockClientInstance.close.resolves(undefined);

            await registry.listTools({ serverId: 'a', command: 'echo' });
            await registry.listTools({ serverId: 'b', command: 'cat' });

            const count = await registry.stopAll();
            expect(count).to.equal(2);

            await registry.dispose();
        });

        it('should describe running servers with idle time', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            await registry.listTools({ serverId: 'desc-srv', command: 'echo' });

            const servers = registry.describeServers() as any[];
            expect(servers).to.have.length(1);
            expect(servers[0].id).to.equal('desc-srv');
            expect(servers[0].idleMs).to.be.a('number');

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // callTool
    // -----------------------------------------------------------------------

    describe('callTool', () => {
        it('should throw when toolName is empty', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            try {
                await registry.callTool({
                    serverId: 'srv',
                    command: 'echo',
                    toolName: '',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e.message).to.include('toolName is required');
            }

            await registry.dispose();
        });

        it('should call the tool and serialize the result', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.callTool.resolves({
                content: [{ type: 'text', text: 'result text' }],
                isError: false,
            });

            const result = await registry.callTool({
                serverId: 'srv-call',
                command: 'echo',
                toolName: 'doThing',
                arguments: { key: 'value' },
            }) as any;

            expect(result.content).to.have.length(1);
            expect(result.content[0].type).to.equal('text');
            expect(result.content[0].text).to.equal('result text');
            expect(result.isError).to.be.false;

            await registry.dispose();
        });

        it('should serialize image content blocks to base64', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const binaryData = new Uint8Array([72, 101, 108, 108, 111]); // "Hello"
            mockClientInstance.callTool.resolves({
                content: [{ type: 'image', data: binaryData, mimeType: 'image/png' }],
                isError: false,
            });

            const result = await registry.callTool({
                serverId: 'srv-img',
                command: 'echo',
                toolName: 'getImage',
            }) as any;

            expect(result.content[0].type).to.equal('image');
            expect(result.content[0].mimeType).to.equal('image/png');
            // base64 of "Hello"
            expect(result.content[0].data).to.equal('SGVsbG8=');

            await registry.dispose();
        });

        it('should handle McpProtocolError as structured error result', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const mcpError = new Error('Protocol error');
            mcpError.name = 'McpError';
            mockClientInstance.callTool.rejects(mcpError);

            const result = await registry.callTool({
                serverId: 'srv-err',
                command: 'echo',
                toolName: 'badTool',
            }) as any;

            expect(result.isError).to.be.true;
            expect(result.content[0].text).to.include('Protocol error');

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // getOrStart validation
    // -----------------------------------------------------------------------

    describe('getOrStart validation', () => {
        it('should throw when serverId is empty', async () => {
            const registry = new McpClientRegistry(() => {});

            try {
                await registry.listTools({ serverId: '', command: 'echo' } as any);
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e.message).to.include('serverId is required');
            }

            await registry.dispose();
        });

        it('should throw when neither command nor url is provided', async () => {
            const registry = new McpClientRegistry(() => {});

            try {
                await registry.listTools({ serverId: 'srv' } as any);
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e.message).to.include('command or url is required');
            }

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // Fingerprint change triggers restart
    // -----------------------------------------------------------------------

    describe('fingerprint change triggers restart', () => {
        it('should restart server when fingerprint changes', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });
            mockClientInstance.close.resolves(undefined);

            // Start with command 'echo'
            await registry.listTools({ serverId: 'srv-restart', command: 'echo' });

            // Change command → fingerprint changes → restart
            await registry.listTools({ serverId: 'srv-restart', command: 'cat' });

            // close should have been called for the old client
            expect(mockClientInstance.close.calledOnce).to.be.true;

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // listTools
    // -----------------------------------------------------------------------

    describe('listTools', () => {
        it('should list tools with name, description, and inputSchema from client.listTools result', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({
                tools: [
                    {
                        name: 'tool1',
                        description: 'First tool',
                        inputSchema: { type: 'object', properties: { x: { type: 'string' } } },
                    },
                    {
                        name: 'tool2',
                        description: 'Second tool',
                        inputSchema: { type: 'object', properties: {} },
                    },
                ],
            });

            const result = await registry.listTools({
                serverId: 'srv-list',
                command: 'echo',
            }) as any;

            expect(result.tools).to.have.length(2);
            expect(result.tools[0].name).to.equal('tool1');
            expect(result.tools[0].description).to.equal('First tool');
            expect(result.tools[0].inputSchema).to.deep.equal({
                type: 'object',
                properties: { x: { type: 'string' } },
            });
            expect(result.tools[1].name).to.equal('tool2');
            expect(result.tools[1].description).to.equal('Second tool');

            await registry.dispose();
        });

        it('should return empty tools array when server returns no tools', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            const result = await registry.listTools({
                serverId: 'srv-empty',
                command: 'echo',
            }) as any;

            expect(result.tools).to.have.length(0);
            expect(result.tools).to.deep.equal([]);

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // withRecovery (transport death recovery)
    // -----------------------------------------------------------------------

    describe('withRecovery (transport death recovery)', () => {
        it('should retry once when transport death error occurs (ECONNRESET), then succeed on retry', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);

            const connResetError = new Error('Connection reset');
            (connResetError as any).code = 'ECONNRESET';

            let callCount = 0;
            mockClientInstance.listTools.callsFake(async () => {
                callCount++;
                if (callCount === 1) {
                    throw connResetError;
                }
                return { tools: [{ name: 'recovered', description: 'd', inputSchema: {} }] };
            });

            const result = await registry.listTools({
                serverId: 'srv-recovery',
                command: 'echo',
            }) as any;

            expect(result.tools).to.have.length(1);
            expect(result.tools[0].name).to.equal('recovered');
            // listTools called twice: first failed, second succeeded
            expect(mockClientInstance.listTools.callCount).to.equal(2);
            // close called once for the dead server
            expect(mockClientInstance.close.calledOnce).to.be.true;

            await registry.dispose();
        });

        it('should NOT retry for McpError (protocol error) - error propagates', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const mcpError = new Error('Protocol error');
            mcpError.name = 'McpError';
            mockClientInstance.listTools.rejects(mcpError);

            try {
                await registry.listTools({
                    serverId: 'srv-mcp',
                    command: 'echo',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e.name).to.equal('McpError');
            }

            // listTools should only have been called once (no retry)
            expect(mockClientInstance.listTools.callCount).to.equal(1);
            // close should NOT have been called (no recovery)
            expect(mockClientInstance.close.called).to.be.false;

            await registry.dispose();
        });

        it('should NOT retry for AbortError - error propagates', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const abortError = new Error('Aborted');
            abortError.name = 'AbortError';
            mockClientInstance.listTools.rejects(abortError);

            try {
                await registry.listTools({
                    serverId: 'srv-abort',
                    command: 'echo',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e.name).to.equal('AbortError');
            }

            expect(mockClientInstance.listTools.callCount).to.equal(1);
            expect(mockClientInstance.close.called).to.be.false;

            await registry.dispose();
        });

        it('should NOT retry for TypeError - error propagates', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const typeError = new TypeError('Bad argument');
            mockClientInstance.listTools.rejects(typeError);

            try {
                await registry.listTools({
                    serverId: 'srv-type',
                    command: 'echo',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e).to.be.instanceOf(TypeError);
            }

            expect(mockClientInstance.listTools.callCount).to.equal(1);
            expect(mockClientInstance.close.called).to.be.false;

            await registry.dispose();
        });

        it('should propagate error if retry also fails (ECONNREFUSED on both attempts)', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);

            const connRefusedError = new Error('Connection refused');
            (connRefusedError as any).code = 'ECONNREFUSED';
            mockClientInstance.listTools.rejects(connRefusedError);

            try {
                await registry.listTools({
                    serverId: 'srv-double-fail',
                    command: 'echo',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e.message).to.include('Connection refused');
            }

            // listTools called twice: first failure triggers retry, second also fails
            expect(mockClientInstance.listTools.callCount).to.equal(2);
            // close called once for the first dead server
            expect(mockClientInstance.close.calledOnce).to.be.true;

            await registry.dispose();
        });

        it('should retry when error message contains "exited unexpectedly"', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);

            const exitError = new Error('Server process exited unexpectedly');
            let callCount = 0;
            mockClientInstance.listTools.callsFake(async () => {
                callCount++;
                if (callCount === 1) {
                    throw exitError;
                }
                return { tools: [] };
            });

            const result = await registry.listTools({
                serverId: 'srv-exit',
                command: 'echo',
            }) as any;

            expect(result.tools).to.deep.equal([]);
            expect(mockClientInstance.listTools.callCount).to.equal(2);
            expect(mockClientInstance.close.calledOnce).to.be.true;

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // HTTP transport start
    // -----------------------------------------------------------------------

    describe('HTTP transport start', () => {
        it('should create StreamableHTTPClientTransport with URL when url is provided', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            await registry.listTools({
                serverId: 'srv-http',
                command: '',
                url: 'http://localhost:3000/mcp',
            });

            // StreamableHTTPClientTransport should have been called
            expect(MockStreamableHTTPTransport.calledOnce).to.be.true;
            const args = MockStreamableHTTPTransport.firstCall.args;
            // First arg should be a URL object
            expect(args[0]).to.be.instanceOf(URL);
            expect((args[0] as URL).href).to.equal('http://localhost:3000/mcp');

            // Stdio transport should NOT have been used
            expect(MockStdioTransport.called).to.be.false;

            await registry.dispose();
        });

        it('should pass headers to StreamableHTTPClientTransport when provided', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            const headers = { Authorization: 'Bearer mytoken', 'X-Custom': 'val' };
            await registry.listTools({
                serverId: 'srv-http-headers',
                command: '',
                url: 'http://localhost:3000/mcp',
                headers,
            });

            expect(MockStreamableHTTPTransport.calledOnce).to.be.true;
            const args = MockStreamableHTTPTransport.firstCall.args;
            // Second arg is options with requestInit.headers
            const opts = args[1];
            expect(opts.requestInit.headers).to.deep.equal(headers);

            await registry.dispose();
        });

        it('should use stdio transport when command is provided (no url)', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            await registry.listTools({
                serverId: 'srv-stdio',
                command: 'echo',
            });

            // StdioClientTransport should have been called
            expect(MockStdioTransport.calledOnce).to.be.true;
            // StreamableHTTPClientTransport should NOT have been used
            expect(MockStreamableHTTPTransport.called).to.be.false;

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // withTimeout
    // -----------------------------------------------------------------------

    describe('withTimeout', () => {
        let clock: sinon.SinonFakeTimers;

        beforeEach(() => {
            clock = sinon.useFakeTimers();
        });

        afterEach(() => {
            clock.restore();
        });

        it('should timeout listTools if it takes too long', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            // listTools returns a promise that never resolves
            mockClientInstance.listTools.returns(new Promise(() => {}));

            const promise = registry.listTools({
                serverId: 'srv-timeout-list',
                command: 'echo',
            });

            // Advance past LIST_TOOLS_TIMEOUT_MS (120000ms) using tickAsync
            // so that microtasks (promise rejections) are processed.
            await clock.tickAsync(120001);

            try {
                await promise;
                expect.fail('Should have timed out');
            } catch (e: any) {
                expect(e.message).to.include('timed out');
                expect(e.message).to.include('120000');
            }

            await registry.dispose();
        });

        it('should timeout callTool with custom timeoutMs', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            // callTool returns a promise that never resolves
            mockClientInstance.callTool.returns(new Promise(() => {}));

            const promise = registry.callTool({
                serverId: 'srv-timeout-call',
                command: 'echo',
                toolName: 'slowTool',
                timeoutMs: 5000,
            });

            // Advance past the custom 5000ms timeout using tickAsync
            // so that microtasks (promise rejections) are processed.
            await clock.tickAsync(5001);

            try {
                await promise;
                expect.fail('Should have timed out');
            } catch (e: any) {
                expect(e.message).to.include('timed out');
                expect(e.message).to.include('5000');
            }

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // buildArguments (tested via callTool)
    // -----------------------------------------------------------------------

    describe('buildArguments (via callTool)', () => {
        it('should return empty object for null/undefined arguments', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.callTool.resolves({ content: [], isError: false });

            // Test with undefined arguments
            await registry.callTool({
                serverId: 'srv-null-args',
                command: 'echo',
                toolName: 'doThing',
            });

            expect(mockClientInstance.callTool.calledOnce).to.be.true;
            const callArgs = mockClientInstance.callTool.firstCall.args[0] as any;
            expect(callArgs.arguments).to.deep.equal({});

            await registry.dispose();
        });

        it('should return empty object for array arguments', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.callTool.resolves({ content: [], isError: false });

            await registry.callTool({
                serverId: 'srv-array-args',
                command: 'echo',
                toolName: 'doThing',
                arguments: ['a', 'b', 'c'],
            });

            expect(mockClientInstance.callTool.calledOnce).to.be.true;
            const callArgs = mockClientInstance.callTool.firstCall.args[0] as any;
            expect(callArgs.arguments).to.deep.equal({});

            await registry.dispose();
        });

        it('should return the object as-is for object arguments', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.callTool.resolves({ content: [], isError: false });

            const args = { key1: 'val1', key2: 42, nested: { a: true } };
            await registry.callTool({
                serverId: 'srv-obj-args',
                command: 'echo',
                toolName: 'doThing',
                arguments: args,
            });

            expect(mockClientInstance.callTool.calledOnce).to.be.true;
            const callArgs = mockClientInstance.callTool.firstCall.args[0] as any;
            expect(callArgs.arguments).to.deep.equal(args);

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // toBase64 (tested via callTool)
    // -----------------------------------------------------------------------

    describe('toBase64 (via callTool)', () => {
        it('should serialize audio content blocks to base64 (Uint8Array data)', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const audioData = new Uint8Array([1, 2, 3, 4, 5]);
            mockClientInstance.callTool.resolves({
                content: [{ type: 'audio', data: audioData, mimeType: 'audio/wav' }],
                isError: false,
            });

            const result = await registry.callTool({
                serverId: 'srv-audio',
                command: 'echo',
                toolName: 'getAudio',
            }) as any;

            expect(result.content[0].type).to.equal('audio');
            expect(result.content[0].mimeType).to.equal('audio/wav');
            // base64 of [1,2,3,4,5]
            expect(result.content[0].data).to.equal(Buffer.from(audioData).toString('base64'));

            await registry.dispose();
        });

        it('should handle ArrayBuffer data in image blocks', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const buffer = new ArrayBuffer(5);
            const view = new Uint8Array(buffer);
            view[0] = 72; view[1] = 101; view[2] = 108; view[3] = 108; view[4] = 111; // "Hello"
            mockClientInstance.callTool.resolves({
                content: [{ type: 'image', data: buffer, mimeType: 'image/png' }],
                isError: false,
            });

            const result = await registry.callTool({
                serverId: 'srv-arraybuf',
                command: 'echo',
                toolName: 'getImage',
            }) as any;

            expect(result.content[0].type).to.equal('image');
            // base64 of "Hello"
            expect(result.content[0].data).to.equal('SGVsbG8=');

            await registry.dispose();
        });

        it('should handle string data (already base64) without conversion', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const base64String = 'SGVsbG8=';
            mockClientInstance.callTool.resolves({
                content: [{ type: 'image', data: base64String, mimeType: 'image/png' }],
                isError: false,
            });

            const result = await registry.callTool({
                serverId: 'srv-str-data',
                command: 'echo',
                toolName: 'getImage',
            }) as any;

            expect(result.content[0].type).to.equal('image');
            expect(result.content[0].data).to.equal(base64String);

            await registry.dispose();
        });

        it('should handle unknown block types by serializing raw', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const unknownBlock = { type: 'custom', foo: 'bar', num: 42 };
            mockClientInstance.callTool.resolves({
                content: [unknownBlock],
                isError: false,
            });

            const result = await registry.callTool({
                serverId: 'srv-unknown',
                command: 'echo',
                toolName: 'customTool',
            }) as any;

            expect(result.content[0]).to.deep.equal(unknownBlock);
            expect(result.content[0].type).to.equal('custom');
            expect(result.content[0].foo).to.equal('bar');

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // serializeToolResult
    // -----------------------------------------------------------------------

    describe('serializeToolResult (via callTool)', () => {
        it('should return isError=false when result has no isError field', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.callTool.resolves({
                content: [{ type: 'text', text: 'ok' }],
                // No isError field
            });

            const result = await registry.callTool({
                serverId: 'srv-no-iserror',
                command: 'echo',
                toolName: 'doThing',
            }) as any;

            expect(result.isError).to.be.false;
            expect(result.content[0].text).to.equal('ok');

            await registry.dispose();
        });

        it('should handle empty content array', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.callTool.resolves({
                content: [],
                isError: false,
            });

            const result = await registry.callTool({
                serverId: 'srv-empty-content',
                command: 'echo',
                toolName: 'doThing',
            }) as any;

            expect(result.content).to.have.length(0);
            expect(result.isError).to.be.false;

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // reapIdle
    // -----------------------------------------------------------------------

    describe('reapIdle', () => {
        let clock: sinon.SinonFakeTimers;

        beforeEach(() => {
            clock = sinon.useFakeTimers();
        });

        afterEach(() => {
            clock.restore();
        });

        it('should stop servers idle longer than 10 minutes', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            // Start a server
            await registry.listTools({ serverId: 'srv-idle', command: 'echo' });

            // Server should be running
            expect(registry.describeServers()).to.have.length(1);

            // Advance time by 11 minutes (660000ms) — past IDLE_LIFETIME_MS (600000ms)
            // This also triggers the reaper interval (every 60s) multiple times.
            await clock.tickAsync(660000);

            // Server should have been reaped
            expect(registry.describeServers()).to.have.length(0);
            expect(mockClientInstance.close.called).to.be.true;

            await registry.dispose();
        });

        it('should NOT stop servers that are still fresh', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            // Start a server
            await registry.listTools({ serverId: 'srv-fresh', command: 'echo' });

            expect(registry.describeServers()).to.have.length(1);

            // Advance time by only 5 minutes (300000ms) — less than IDLE_LIFETIME_MS
            await clock.tickAsync(300000);

            // Server should still be running
            expect(registry.describeServers()).to.have.length(1);
            // close should NOT have been called by the reaper
            expect(mockClientInstance.close.called).to.be.false;

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // dispose
    // -----------------------------------------------------------------------

    describe('dispose', () => {
        it('should clear the reaper timer and stop all servers', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            // Start two servers
            await registry.listTools({ serverId: 'srv-a', command: 'echo' });
            await registry.listTools({ serverId: 'srv-b', command: 'cat' });

            expect(registry.describeServers()).to.have.length(2);

            await registry.dispose();

            // All servers should be stopped
            expect(registry.describeServers()).to.have.length(0);
            // close should have been called for both servers
            expect(mockClientInstance.close.callCount).to.equal(2);
        });

        it('should be safe to call dispose twice', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });

            await registry.listTools({ serverId: 'srv-double', command: 'echo' });

            // First dispose
            await registry.dispose();
            expect(registry.describeServers()).to.have.length(0);

            // Second dispose should not throw
            await registry.dispose();
            expect(registry.describeServers()).to.have.length(0);
        });
    });

    // -----------------------------------------------------------------------
    // disposeClient error handling (via stopServer with failing close)
    // -----------------------------------------------------------------------

    describe('disposeClient error handling', () => {
        it('should log warning when client.close() throws during stopServer', async () => {
            const logs: string[] = [];
            const registry = new McpClientRegistry((msg: string) => logs.push(msg));
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.listTools.resolves({ tools: [] });
            mockClientInstance.close.rejects(new Error('close failed'));

            await registry.listTools({ serverId: 'srv-close-err', command: 'echo' });

            // stopServer should still return true even if close fails
            const stopped = await registry.stopServer('srv-close-err');
            expect(stopped).to.be.true;

            // Warning should have been logged
            expect(logs.some(l => l.includes('WARN') && l.includes('close failed'))).to.be.true;

            await registry.dispose();
        });

        it('should log warning when client.close() throws during reapIdle', async () => {
            const logs: string[] = [];
            const clock = sinon.useFakeTimers();
            const registry = new McpClientRegistry((msg: string) => logs.push(msg));
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.rejects(new Error('reap close error'));
            mockClientInstance.listTools.resolves({ tools: [] });

            await registry.listTools({ serverId: 'srv-reap-err', command: 'echo' });

            // Advance past idle lifetime to trigger reaper
            await clock.tickAsync(660000);

            // Warning should have been logged
            expect(logs.some(l => l.includes('WARN') && l.includes('reap close error'))).to.be.true;

            clock.restore();
            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // errorMessage / isMcpProtocolError edge cases (via callTool)
    // -----------------------------------------------------------------------

    describe('errorMessage and isMcpProtocolError edge cases', () => {
        it('should handle non-Error thrown values in callTool (string error)', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            // Reject with a plain string (not an Error) using callsFake
            // because sinon.rejects(string) wraps it in new Error(string)
            mockClientInstance.callTool.callsFake(async () => {
                throw 'plain string error';
            });

            try {
                await registry.callTool({
                    serverId: 'srv-str-err',
                    command: 'echo',
                    toolName: 'doThing',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                // The string error is not an McpError, not transport death,
                // so it propagates as-is
                expect(e).to.equal('plain string error');
            }

            await registry.dispose();
        });

        it('should handle non-Error thrown values in callTool (number error)', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            // Throw a number (not an Error)
            mockClientInstance.callTool.callsFake(async () => {
                throw 42;
            });

            try {
                await registry.callTool({
                    serverId: 'srv-num-err',
                    command: 'echo',
                    toolName: 'doThing',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e).to.equal(42);
            }

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // toBase64 edge cases (via callTool)
    // -----------------------------------------------------------------------

    describe('toBase64 edge cases (via callTool)', () => {
        it('should handle ArrayBufferView (Int32Array) data in image blocks', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            // Int32Array is an ArrayBufferView but not Uint8Array
            const int32Data = new Int32Array([0x41414141, 0x42424242]);
            mockClientInstance.callTool.resolves({
                content: [{ type: 'image', data: int32Data, mimeType: 'image/png' }],
                isError: false,
            });

            const result = await registry.callTool({
                serverId: 'srv-int32',
                command: 'echo',
                toolName: 'getImage',
            }) as any;

            expect(result.content[0].type).to.equal('image');
            // Should be base64 encoded
            expect(result.content[0].data).to.be.a('string');
            expect(result.content[0].data).to.equal(
                Buffer.from(int32Data.buffer as ArrayBuffer, int32Data.byteOffset, int32Data.byteLength).toString('base64'),
            );

            await registry.dispose();
        });

        it('should JSON-stringify unknown data types in toBase64 fallback', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            // Pass an object that is not string, Uint8Array, ArrayBuffer, or ArrayBufferView
            const objData = { nested: 'object' };
            mockClientInstance.callTool.resolves({
                content: [{ type: 'image', data: objData, mimeType: 'image/png' }],
                isError: false,
            });

            const result = await registry.callTool({
                serverId: 'srv-obj-data',
                command: 'echo',
                toolName: 'getImage',
            }) as any;

            expect(result.content[0].type).to.equal('image');
            // Fallback: JSON.stringify
            expect(result.content[0].data).to.equal(JSON.stringify(objData));

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // Start failure
    // -----------------------------------------------------------------------

    describe('start failure', () => {
        it('should throw when client.connect() fails', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.rejects(new Error('Connection refused by server'));

            try {
                await registry.listTools({
                    serverId: 'srv-connect-fail',
                    command: 'echo',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e.message).to.include('Failed to start or initialize');
                expect(e.message).to.include('Connection refused by server');
            }

            await registry.dispose();
        });

        it('should throw when resolveCommand returns null for stdio transport', async () => {
            const registry = new McpClientRegistry(() => {});
            // resolveCommand is stubbed in beforeEach to return '/usr/bin/echo'
            // Override it to return null
            resolveCommandStub!.restore();
            resolveCommandStub = sinon.stub(McpClientRegistry, 'resolveCommand').returns(null);

            try {
                await registry.listTools({
                    serverId: 'srv-not-found',
                    command: 'nonexistent-command',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e.message).to.include('Failed to find');
                expect(e.message).to.include('nonexistent-command');
            }

            await registry.dispose();
        });
    });

    // -----------------------------------------------------------------------
    // isTransportDeath edge cases (via listTools)
    // -----------------------------------------------------------------------

    describe('isTransportDeath edge cases', () => {
        it('should retry on EPIPE error code', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);

            const pipeError = new Error('Broken pipe');
            (pipeError as any).code = 'EPIPE';
            let callCount = 0;
            mockClientInstance.listTools.callsFake(async () => {
                callCount++;
                if (callCount === 1) throw pipeError;
                return { tools: [] };
            });

            const result = await registry.listTools({
                serverId: 'srv-pipe',
                command: 'echo',
            }) as any;

            expect(result.tools).to.deep.equal([]);
            expect(mockClientInstance.listTools.callCount).to.equal(2);

            await registry.dispose();
        });

        it('should retry on ERR_STREAM_DESTROYED error code', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);

            const destroyedError = new Error('Stream destroyed');
            (destroyedError as any).code = 'ERR_STREAM_DESTROYED';
            let callCount = 0;
            mockClientInstance.listTools.callsFake(async () => {
                callCount++;
                if (callCount === 1) throw destroyedError;
                return { tools: [] };
            });

            await registry.listTools({
                serverId: 'srv-destroyed',
                command: 'echo',
            });

            expect(mockClientInstance.listTools.callCount).to.equal(2);
            await registry.dispose();
        });

        it('should retry on ERR_CLOSED error code', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);
            mockClientInstance.close.resolves(undefined);

            const closedError = new Error('Connection closed');
            (closedError as any).code = 'ERR_CLOSED';
            let callCount = 0;
            mockClientInstance.listTools.callsFake(async () => {
                callCount++;
                if (callCount === 1) throw closedError;
                return { tools: [] };
            });

            await registry.listTools({
                serverId: 'srv-closed',
                command: 'echo',
            });

            expect(mockClientInstance.listTools.callCount).to.equal(2);
            await registry.dispose();
        });

        it('should NOT retry for RangeError', async () => {
            const registry = new McpClientRegistry(() => {});
            mockClientInstance.connect.resolves(undefined);

            const rangeError = new RangeError('Out of range');
            mockClientInstance.listTools.rejects(rangeError);

            try {
                await registry.listTools({
                    serverId: 'srv-range',
                    command: 'echo',
                });
                expect.fail('Should have thrown');
            } catch (e: any) {
                expect(e).to.be.instanceOf(RangeError);
            }

            expect(mockClientInstance.listTools.callCount).to.equal(1);
            await registry.dispose();
        });
    });
});
