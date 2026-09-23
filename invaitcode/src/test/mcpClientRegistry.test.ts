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
});
