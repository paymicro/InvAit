/**
 * Unit tests for toolDispatcher.ts
 *
 * Tests handleVsRequest, setOnUiReady, and disposeMcpRegistry.
 * All external dependencies (vscode, fs, child_process, os, toolHandlers,
 * mcpClientRegistry, contextPublisher, logger) are mocked via Module._load.
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import * as path from 'path';

chai.use(chaiAsPromised);
const expect = chai.expect;

// ---------------------------------------------------------------------------
// Mock dependencies BEFORE importing toolDispatcher.
// ---------------------------------------------------------------------------

// --- vscode mock (comprehensive, inline) ---

const vscodeMock: any = {
    window: {
        activeTextEditor: undefined as any,
        showTextDocument: sinon.stub(),
        onDidChangeActiveTextEditor: sinon.stub(),
        onDidChangeTextEditorSelection: sinon.stub(),
    },
    workspace: {
        workspaceFolders: undefined as any,
        openTextDocument: sinon.stub().resolves({}),
        onDidChangeWorkspaceFolders: sinon.stub(),
        onDidSaveTextDocument: sinon.stub(),
        fs: {},
    },
    commands: {
        registerCommand: sinon.stub(),
        executeCommand: sinon.stub(),
    },
    languages: {
        getDiagnostics: sinon.stub().returns([]),
    },
    DiagnosticSeverity: { Error: 0, Warning: 1, Information: 2, Hint: 3 },
    SymbolKind: (function () {
        // Build a TypeScript-style enum object that supports both
        // forward (SymbolKind.Class -> 4) and reverse (SymbolKind[4] -> 'Class') lookups.
        const entries: [string, number][] = [
            ['File', 0], ['Module', 1], ['Namespace', 2], ['Package', 3], ['Class', 4],
            ['Method', 5], ['Property', 6], ['Field', 7], ['Constructor', 8], ['Enum', 9],
            ['Interface', 10], ['Function', 11], ['Variable', 12], ['Constant', 13],
            ['String', 14], ['Number', 15], ['Boolean', 16], ['Array', 17], ['Object', 18],
            ['Key', 19], ['Null', 20], ['EnumMember', 21], ['Struct', 22], ['Event', 23],
            ['Operator', 24], ['TypeParameter', 25],
        ];
        const obj: any = {};
        for (const [name, val] of entries) {
            obj[name] = val;
            obj[val] = name;
        }
        return obj;
    })(),
    Uri: { file: (p: string) => ({ fsPath: p }) },
    WebviewPanel: class {},
};

// --- fs mock ---

const fsStub = {
    existsSync: sinon.stub(),
    readFileSync: sinon.stub(),
    writeFileSync: sinon.stub(),
    mkdirSync: sinon.stub(),
    statSync: sinon.stub(),
    unlinkSync: sinon.stub(),
    readdirSync: sinon.stub(),
};

// --- os mock ---

const osStub = {
    homedir: sinon.stub().returns('/home/user'),
    tmpdir: sinon.stub().returns('/tmp'),
    platform: sinon.stub().returns('linux'),
};

// --- child_process mock ---

const childProcessStub = {
    exec: sinon.stub(),
    execSync: sinon.stub(),
    spawn: sinon.stub().returns({ unref: sinon.stub() }),
    spawnSync: sinon.stub(),
};

// --- toolHandlers mock ---

const dispatchToolStub = sinon.stub();
const toolHandlersMock = {
    dispatchTool: dispatchToolStub,
    ToolResult: class {},
    buildWorkspaceFiles: sinon.stub().returns([]),
};

// --- mcpClientRegistry mock ---

const mockMcpInstance = {
    listTools: sinon.stub(),
    callTool: sinon.stub(),
    stopAll: sinon.stub().resolves(0),
    dispose: sinon.stub().resolves(),
};
const MockMcpClientRegistry = sinon.stub().returns(mockMcpInstance);

// --- contextPublisher mock ---

const invalidateContextCacheStub = sinon.stub();
const contextPublisherMock = {
    invalidateContextCache: invalidateContextCacheStub,
};

// --- logger mock ---

const loggerMock = {
    log: sinon.stub(),
    logError: sinon.stub(),
    logWarning: sinon.stub(),
    initLogger: sinon.stub(),
    getChannel: sinon.stub().returns(null),
    showOutput: sinon.stub(),
    disposeLogger: sinon.stub(),
};

// --- Intercept Module._load ---

const Module = require('module');
const originalLoad = Module._load;

Module._load = function (request: string, parent: any, isMain: boolean) {
    if (request === 'vscode') return vscodeMock;
    if (request === 'fs') return fsStub;
    if (request === 'os') return osStub;
    if (request === 'child_process') return childProcessStub;
    if (request === './toolHandlers' || request === '../toolHandlers') return toolHandlersMock;
    if (request === './mcpClientRegistry' || request === '../mcpClientRegistry') return { McpClientRegistry: MockMcpClientRegistry };
    if (request === './contextPublisher' || request === '../contextPublisher') return contextPublisherMock;
    if (request === './logger' || request === '../logger') return loggerMock;
    return originalLoad.call(this, request, parent, isMain);
};

// Now import the module under test — it will receive our stubs.
import {
    handleVsRequest,
    setOnUiReady,
    disposeMcpRegistry,
    VsRequestLike,
    VsResponsePayload,
} from '../toolDispatcher';

// Restore after import
Module._load = originalLoad;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const WORKSPACE = path.sep === '\\' ? 'B:\\ws' : '/ws';

function createFakePanel(): any {
    return {
        webview: {
            postMessage: sinon.stub(),
        },
    };
}

function createMessage(action: string, correlationId: string, payload?: string): VsRequestLike {
    return { action, correlationId, payload };
}

function getLastResponse(panel: any): VsResponsePayload {
    const call = panel.webview.postMessage.getCall(panel.webview.postMessage.callCount - 1);
    return call.args[0].payload;
}

// ---------------------------------------------------------------------------

describe('toolDispatcher', () => {

    let originalPlatform: string;

    beforeEach(() => {
        // Set up workspace folders
        vscodeMock.workspace.workspaceFolders = [{ uri: { fsPath: WORKSPACE } }];
        vscodeMock.window.activeTextEditor = undefined;
        originalPlatform = process.platform;
    });

    afterEach(() => {
        sinon.restore();
        // Reset all stubs
        vscodeMock.window.showTextDocument.resetHistory();
        vscodeMock.workspace.openTextDocument.resetHistory();
        vscodeMock.commands.executeCommand.resetHistory();
        vscodeMock.languages.getDiagnostics.resetHistory();
        vscodeMock.languages.getDiagnostics.returns([]);

        fsStub.existsSync.reset();
        fsStub.readFileSync.reset();
        fsStub.writeFileSync.reset();
        fsStub.mkdirSync.reset();
        fsStub.statSync.reset();
        fsStub.unlinkSync.reset();
        fsStub.readdirSync.reset();

        osStub.homedir.reset();
        osStub.homedir.returns('/home/user');

        childProcessStub.exec.resetHistory();
        childProcessStub.spawn.resetHistory();
        childProcessStub.spawn.returns({ unref: sinon.stub() });

        dispatchToolStub.resetHistory();
        invalidateContextCacheStub.resetHistory();

        mockMcpInstance.listTools.resetHistory();
        mockMcpInstance.callTool.resetHistory();
        mockMcpInstance.stopAll.resetHistory();
        mockMcpInstance.stopAll.resolves(0);
        mockMcpInstance.dispose.resetHistory();
        mockMcpInstance.dispose.resolves();

        // NOTE: Do NOT reset MockMcpClientRegistry history — it is called once
        // at module load time and we need that call recorded for the
        // 'McpClientRegistry instantiation' test.

        loggerMock.log.resetHistory();
        loggerMock.logError.resetHistory();

        // Restore process.platform
        Object.defineProperty(process, 'platform', {
            value: originalPlatform,
            writable: true,
            configurable: true,
        });
    });

    // -----------------------------------------------------------------------
    // ui_ready
    // -----------------------------------------------------------------------

    describe('ui_ready', () => {
        it('should call onUiReady callback with workspace root and send OK response', async () => {
            const callback = sinon.stub();
            setOnUiReady(callback);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('ui_ready', 'corr-1'));

            expect(callback.calledOnce).to.be.true;
            expect(callback.firstCall.args[0]).to.equal(WORKSPACE);

            const response = getLastResponse(panel);
            expect(response.correlationId).to.equal('corr-1');
            expect(response.success).to.be.true;
            expect(response.payload).to.equal('OK');
        });

        it('should send OK response even if onUiReady is not set', async () => {
            setOnUiReady(null as any);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('ui_ready', 'corr-2'));

            const response = getLastResponse(panel);
            expect(response.correlationId).to.equal('corr-2');
            expect(response.success).to.be.true;
            expect(response.payload).to.equal('OK');
        });
    });

    // -----------------------------------------------------------------------
    // read_open_file
    // -----------------------------------------------------------------------

    describe('read_open_file', () => {
        it('should read active editor file content and return lines', async () => {
            const fileContent = 'line1\nline2\nline3';
            fsStub.readFileSync.returns(fileContent);
            vscodeMock.window.activeTextEditor = {
                document: { fileName: '/path/to/file.ts' },
            };

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('read_open_file', 'corr-1'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            const parsed = JSON.parse(response.payload!);
            expect(parsed).to.have.length(1);
            expect(parsed[0].path).to.equal('/path/to/file.ts');
            expect(parsed[0].lines).to.deep.equal(['line1', 'line2', 'line3']);
        });

        it('should truncate to 2000 lines', async () => {
            const lines: string[] = [];
            for (let i = 0; i < 2500; i++) {
                lines.push(`line${i}`);
            }
            fsStub.readFileSync.returns(lines.join('\n'));
            vscodeMock.window.activeTextEditor = {
                document: { fileName: '/path/to/big.ts' },
            };

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('read_open_file', 'corr-2'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            const parsed = JSON.parse(response.payload!);
            expect(parsed[0].lines).to.have.length(2000);
            expect(parsed[0].totalLines).to.equal(2500);
        });

        it('should return error when no active document', async () => {
            vscodeMock.window.activeTextEditor = undefined;

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('read_open_file', 'corr-3'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.equal('No active document');
        });
    });

    // -----------------------------------------------------------------------
    // open_file
    // -----------------------------------------------------------------------

    describe('open_file', () => {
        it('should open file with absolute path', async () => {
            const absPath = path.sep === '\\' ? 'B:\\abs\\file.ts' : '/abs/file.ts';
            fsStub.existsSync.returns(true);
            vscodeMock.workspace.openTextDocument.resolves({});

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('open_file', 'corr-1', absPath));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('Opened');
            expect(vscodeMock.workspace.openTextDocument.calledOnce).to.be.true;
        });

        it('should open file with relative path joined with workspace root', async () => {
            fsStub.existsSync.returns(true);
            vscodeMock.workspace.openTextDocument.resolves({});

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('open_file', 'corr-2', 'src/file.ts'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            const openedPath = vscodeMock.workspace.openTextDocument.firstCall.args[0];
            expect(openedPath).to.equal(path.join(WORKSPACE, 'src/file.ts'));
        });

        it('should create directory and file if they don\'t exist', async () => {
            // dir doesn't exist, file doesn't exist
            fsStub.existsSync.returns(false);
            fsStub.mkdirSync.returns(undefined);
            fsStub.writeFileSync.returns(undefined);
            vscodeMock.workspace.openTextDocument.resolves({});

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('open_file', 'corr-3', 'newdir/newfile.ts'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(fsStub.mkdirSync.calledOnce).to.be.true;
            expect(fsStub.writeFileSync.calledOnce).to.be.true;
        });
    });

    // -----------------------------------------------------------------------
    // open_folder
    // -----------------------------------------------------------------------

    describe('open_folder', () => {
        it('should spawn explorer.exe on Windows', async () => {
            Object.defineProperty(process, 'platform', { value: 'win32', configurable: true });
            const unrefStub = sinon.stub();
            childProcessStub.spawn.returns({ unref: unrefStub });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('open_folder', 'corr-1', 'C:\\folder'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(childProcessStub.spawn.calledOnce).to.be.true;
            expect(childProcessStub.spawn.firstCall.args[0]).to.equal('explorer.exe');
            expect(unrefStub.calledOnce).to.be.true;
        });

        it('should spawn open on macOS', async () => {
            Object.defineProperty(process, 'platform', { value: 'darwin', configurable: true });
            const unrefStub = sinon.stub();
            childProcessStub.spawn.returns({ unref: unrefStub });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('open_folder', 'corr-2', '/folder'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(childProcessStub.spawn.firstCall.args[0]).to.equal('open');
            expect(unrefStub.calledOnce).to.be.true;
        });

        it('should spawn xdg-open on Linux', async () => {
            Object.defineProperty(process, 'platform', { value: 'linux', configurable: true });
            const unrefStub = sinon.stub();
            childProcessStub.spawn.returns({ unref: unrefStub });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('open_folder', 'corr-3', '/folder'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(childProcessStub.spawn.firstCall.args[0]).to.equal('xdg-open');
            expect(unrefStub.calledOnce).to.be.true;
        });
    });

    // -----------------------------------------------------------------------
    // get_error_list
    // -----------------------------------------------------------------------

    describe('get_error_list', () => {
        it('should return "No errors" when no diagnostics', async () => {
            vscodeMock.languages.getDiagnostics.returns([]);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('get_error_list', 'corr-1'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.equal('No errors');
        });

        it('should return formatted errors from diagnostics', async () => {
            vscodeMock.languages.getDiagnostics.returns([
                [
                    { fsPath: '/path/file.ts' },
                    [
                        { severity: 0, message: 'Type error', range: { start: { line: 5 } } },
                        { severity: 1, message: 'Warning msg', range: { start: { line: 10 } } },
                    ],
                ],
            ]);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('get_error_list', 'corr-2'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('/path/file.ts');
            expect(response.payload).to.include('line:6');
            expect(response.payload).to.include('Type error');
            // Warning (severity 1) should NOT be included
            expect(response.payload).to.not.include('Warning msg');
        });
    });

    // -----------------------------------------------------------------------
    // build
    // -----------------------------------------------------------------------

    describe('build', () => {
        it('should exec "dotnet build" and return success response', async () => {
            childProcessStub.exec.callsFake((cmd: string, opts: any, cb: Function) => {
                cb(null, 'Build succeeded', '');
                return undefined;
            });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('build', 'corr-1'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('Build successful');
            expect(response.payload).to.include('Build succeeded');
            expect(childProcessStub.exec.firstCall.args[0]).to.equal('dotnet build');
        });

        it('should return error response on build failure', async () => {
            const buildError = new Error('Build failed');
            childProcessStub.exec.callsFake((cmd: string, opts: any, cb: Function) => {
                cb(buildError, '', 'error output');
                return undefined;
            });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('build', 'corr-2'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.include('error output');
        });
    });

    // -----------------------------------------------------------------------
    // run_tests
    // -----------------------------------------------------------------------

    describe('run_tests', () => {
        it('should exec "dotnet test" and return response', async () => {
            childProcessStub.exec.callsFake((cmd: string, opts: any, cb: Function) => {
                cb(null, 'Tests passed', '');
                return undefined;
            });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('run_tests', 'corr-1'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('Tests passed');
            expect(childProcessStub.exec.firstCall.args[0]).to.include('dotnet test');
        });
    });

    // -----------------------------------------------------------------------
    // open_mcp_settings
    // -----------------------------------------------------------------------

    describe('open_mcp_settings', () => {
        it('should create .agents dir and mcp.json if they don\'t exist, then open file', async () => {
            // existsSync returns false for both dir and file checks
            fsStub.existsSync.returns(false);
            fsStub.mkdirSync.returns(undefined);
            fsStub.writeFileSync.returns(undefined);
            vscodeMock.workspace.openTextDocument.resolves({});

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('open_mcp_settings', 'corr-1'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('mcp.json');
            expect(fsStub.mkdirSync.calledOnce).to.be.true;
            expect(fsStub.writeFileSync.calledOnce).to.be.true;
            const writtenContent = fsStub.writeFileSync.firstCall.args[1];
            expect(writtenContent).to.include('"mcp"');
            expect(vscodeMock.workspace.openTextDocument.calledOnce).to.be.true;
        });
    });

    // -----------------------------------------------------------------------
    // read_mcp_settings_file
    // -----------------------------------------------------------------------

    describe('read_mcp_settings_file', () => {
        it('should stop all MCP servers and return file content', async () => {
            fsStub.existsSync.returns(true);
            fsStub.readFileSync.returns('{"mcpServers":{"srv":{}}}');

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('read_mcp_settings_file', 'corr-1'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('mcpServers');
            expect(mockMcpInstance.stopAll.calledOnce).to.be.true;
        });

        it('should return default JSON when mcp.json doesn\'t exist', async () => {
            fsStub.existsSync.returns(false);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('read_mcp_settings_file', 'corr-2'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.equal('{"mcpServers":{}}');
        });
    });

    // -----------------------------------------------------------------------
    // write_mcp_settings
    // -----------------------------------------------------------------------

    describe('write_mcp_settings', () => {
        it('should return error for empty content', async () => {
            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('write_mcp_settings', 'corr-1', ''));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.equal('Content is empty');
        });

        it('should return error for whitespace-only content', async () => {
            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('write_mcp_settings', 'corr-2', '   '));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.equal('Content is empty');
        });

        it('should stop all MCP servers and write content', async () => {
            const content = '{"mcpServers":{"srv":{}}}';
            fsStub.existsSync.returns(false);
            fsStub.mkdirSync.returns(undefined);
            fsStub.writeFileSync.returns(undefined);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('write_mcp_settings', 'corr-3', content));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.equal('OK');
            expect(mockMcpInstance.stopAll.calledOnce).to.be.true;
            expect(fsStub.writeFileSync.calledOnce).to.be.true;
            expect(fsStub.writeFileSync.firstCall.args[1]).to.equal(content);
        });
    });

    // -----------------------------------------------------------------------
    // mcp_get_tools
    // -----------------------------------------------------------------------

    describe('mcp_get_tools', () => {
        it('should return error when serverId is missing', async () => {
            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('mcp_get_tools', 'corr-1', JSON.stringify({})));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.equal('serverId is required');
        });

        it('should call mcpRegistry.listTools and return result', async () => {
            const toolsResult = { tools: [{ name: 'tool1', description: 'desc' }] };
            mockMcpInstance.listTools.resolves(toolsResult);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('mcp_get_tools', 'corr-2', JSON.stringify({
                serverId: 'my-server',
                command: 'npx',
                args: ['-y', 'server'],
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            const parsed = JSON.parse(response.payload!);
            expect(parsed.tools).to.have.length(1);
            expect(parsed.tools[0].name).to.equal('tool1');
            expect(mockMcpInstance.listTools.calledOnce).to.be.true;
            const callArgs = mockMcpInstance.listTools.firstCall.args[0];
            expect(callArgs.serverId).to.equal('my-server');
            expect(callArgs.command).to.equal('npx');
            expect(callArgs.workingDirectory).to.equal(WORKSPACE);
        });
    });

    // -----------------------------------------------------------------------
    // mcp_call_tool
    // -----------------------------------------------------------------------

    describe('mcp_call_tool', () => {
        it('should return error when serverId or toolName is missing', async () => {
            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('mcp_call_tool', 'corr-1', JSON.stringify({
                serverId: 'srv',
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.equal('serverId and toolName are required');
        });

        it('should return error when toolName is missing', async () => {
            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('mcp_call_tool', 'corr-2', JSON.stringify({
                serverId: 'srv',
                toolName: '',
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.equal('serverId and toolName are required');
        });

        it('should call mcpRegistry.callTool and return result', async () => {
            const callResult = { content: [{ type: 'text', text: 'result' }], isError: false };
            mockMcpInstance.callTool.resolves(callResult);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('mcp_call_tool', 'corr-3', JSON.stringify({
                serverId: 'my-server',
                toolName: 'doThing',
                command: 'npx',
                arguments: { key: 'value' },
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            const parsed = JSON.parse(response.payload!);
            expect(parsed.content).to.have.length(1);
            expect(parsed.content[0].text).to.equal('result');
            expect(mockMcpInstance.callTool.calledOnce).to.be.true;
            const callArgs = mockMcpInstance.callTool.firstCall.args[0];
            expect(callArgs.serverId).to.equal('my-server');
            expect(callArgs.toolName).to.equal('doThing');
            expect(callArgs.arguments).to.deep.equal({ key: 'value' });
        });
    });

    // -----------------------------------------------------------------------
    // mcp_stop_all
    // -----------------------------------------------------------------------

    describe('mcp_stop_all', () => {
        it('should call mcpRegistry.stopAll and return count', async () => {
            mockMcpInstance.stopAll.resolves(3);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('mcp_stop_all', 'corr-1'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.equal('Stopped 3 MCP server(s).');
            expect(mockMcpInstance.stopAll.calledOnce).to.be.true;
        });
    });

    // -----------------------------------------------------------------------
    // find_declarations
    // -----------------------------------------------------------------------

    describe('find_declarations', () => {
        it('should return error when symbol is missing', async () => {
            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('find_declarations', 'corr-1', JSON.stringify({})));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.equal('Symbol name is required.');
        });

        it('should return error when symbol not found', async () => {
            vscodeMock.commands.executeCommand.resolves([]);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('find_declarations', 'corr-2', JSON.stringify({
                symbol: 'NonExistent',
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.include("isn't found");
        });

        it('should return formatted declarations', async () => {
            vscodeMock.commands.executeCommand.resolves([
                {
                    name: 'MyClass',
                    kind: 4, // Class
                    containerName: 'MyNamespace',
                    location: { uri: { fsPath: '/src/MyClass.ts' }, range: { start: { line: 10 } } },
                },
                {
                    name: 'MyFunc',
                    kind: 11, // Function
                    containerName: '',
                    location: { uri: { fsPath: '/src/MyFunc.ts' }, range: { start: { line: 20 } } },
                },
            ]);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('find_declarations', 'corr-3', JSON.stringify({
                symbol: 'My',
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('Found 2 declarations');
            expect(response.payload).to.include('MyClass');
            expect(response.payload).to.include('Class');
            expect(response.payload).to.include('MyNamespace');
            expect(response.payload).to.include('/src/MyClass.ts:11');
            expect(response.payload).to.include('MyFunc');
            expect(response.payload).to.include('Function');
            expect(response.payload).to.include('/src/MyFunc.ts:21');
        });

        it('should limit results to 50', async () => {
            const symbols: any[] = [];
            for (let i = 0; i < 60; i++) {
                symbols.push({
                    name: `Symbol${i}`,
                    kind: 4,
                    containerName: '',
                    location: { uri: { fsPath: `/src/file${i}.ts` }, range: { start: { line: i } } },
                });
            }
            vscodeMock.commands.executeCommand.resolves(symbols);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('find_declarations', 'corr-4', JSON.stringify({
                symbol: 'Symbol',
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('showing first 50');
            expect(response.payload).to.include('+10 more results');
        });
    });

    // -----------------------------------------------------------------------
    // find_references
    // -----------------------------------------------------------------------

    describe('find_references', () => {
        it('should return error when symbol is missing', async () => {
            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('find_references', 'corr-1', JSON.stringify({})));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.equal('Symbol name is required.');
        });

        it('should return error when symbol not found', async () => {
            vscodeMock.commands.executeCommand.resolves([]);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('find_references', 'corr-2', JSON.stringify({
                symbol: 'NonExistent',
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.include("isn't found");
        });

        it('should return "No references found" when no refs', async () => {
            // First call: workspace symbol provider returns a symbol
            // Second call: reference provider returns empty
            vscodeMock.commands.executeCommand
                .onCall(0).resolves([
                    {
                        name: 'MyClass',
                        kind: 4,
                        containerName: '',
                        location: { uri: { fsPath: '/src/MyClass.ts' }, range: { start: { line: 10 } } },
                    },
                ])
                .onCall(1).resolves([]);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('find_references', 'corr-3', JSON.stringify({
                symbol: 'MyClass',
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('No references found');
        });

        it('should return formatted references with code lines', async () => {
            // First call: workspace symbol provider
            // Second call: reference provider
            vscodeMock.commands.executeCommand
                .onCall(0).resolves([
                    {
                        name: 'MyClass',
                        kind: 4,
                        containerName: '',
                        location: { uri: { fsPath: '/src/MyClass.ts' }, range: { start: { line: 10 } } },
                    },
                ])
                .onCall(1).resolves([
                    { uri: { fsPath: '/src/usage1.ts' }, range: { start: { line: 5 } } },
                    { uri: { fsPath: '/src/usage2.ts' }, range: { start: { line: 15 } } },
                ]);

            // openTextDocument is called for each ref to read the code line
            vscodeMock.workspace.openTextDocument
                .onCall(0).resolves({
                    lineCount: 100,
                    lineAt: sinon.stub().returns({ text: 'const x = new MyClass();' }),
                })
                .onCall(1).resolves({
                    lineCount: 100,
                    lineAt: sinon.stub().returns({ text: 'let y: MyClass;' }),
                });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('find_references', 'corr-4', JSON.stringify({
                symbol: 'MyClass',
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('Found 2 references');
            expect(response.payload).to.include('/src/usage1.ts:6');
            expect(response.payload).to.include('const x = new MyClass();');
            expect(response.payload).to.include('/src/usage2.ts:16');
            expect(response.payload).to.include('let y: MyClass;');
        });

        it('should limit references to 50', async () => {
            const symbols = [
                {
                    name: 'MyClass',
                    kind: 4,
                    containerName: '',
                    location: { uri: { fsPath: '/src/MyClass.ts' }, range: { start: { line: 10 } } },
                },
            ];
            const refs: any[] = [];
            for (let i = 0; i < 60; i++) {
                refs.push({ uri: { fsPath: `/src/ref${i}.ts` }, range: { start: { line: i } } });
            }

            vscodeMock.commands.executeCommand
                .onCall(0).resolves(symbols)
                .onCall(1).resolves(refs);

            // openTextDocument resolves for each ref
            vscodeMock.workspace.openTextDocument.resolves({
                lineCount: 100,
                lineAt: sinon.stub().returns({ text: 'some code' }),
            });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('find_references', 'corr-5', JSON.stringify({
                symbol: 'MyClass',
            })));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.include('showing first 50');
            expect(response.payload).to.include('+10 more results');
        });
    });

    // -----------------------------------------------------------------------
    // Fallback to dispatchTool
    // -----------------------------------------------------------------------

    describe('fallback to dispatchTool', () => {
        it('should delegate unknown action to dispatchTool', async () => {
            dispatchToolStub.returns({ success: true, payload: 'delegated result' });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('read_files', 'corr-1', '{"files":[]}'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.true;
            expect(response.payload).to.equal('delegated result');
            expect(dispatchToolStub.calledOnce).to.be.true;
            expect(dispatchToolStub.firstCall.args[0]).to.equal('read_files');
            expect(dispatchToolStub.firstCall.args[1]).to.equal('{"files":[]}');
            expect(dispatchToolStub.firstCall.args[2]).to.equal(WORKSPACE);
        });

        it('should call invalidateContextCache for create_file', async () => {
            dispatchToolStub.returns({ success: true, payload: 'created' });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('create_file', 'corr-1', '{"filePath":"test.ts"}'));

            expect(invalidateContextCacheStub.calledOnce).to.be.true;
        });

        it('should call invalidateContextCache for edit_files', async () => {
            dispatchToolStub.returns({ success: true, payload: 'edited' });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('edit_files', 'corr-1', '{"filePath":"test.ts"}'));

            expect(invalidateContextCacheStub.calledOnce).to.be.true;
        });

        it('should call invalidateContextCache for delete_file', async () => {
            dispatchToolStub.returns({ success: true, payload: 'deleted' });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('delete_file', 'corr-1', '{"path":"test.ts"}'));

            expect(invalidateContextCacheStub.calledOnce).to.be.true;
        });

        it('should NOT call invalidateContextCache for non-fs-modifying actions', async () => {
            dispatchToolStub.returns({ success: true, payload: 'ok' });

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('read_files', 'corr-1', '{}'));

            expect(invalidateContextCacheStub.called).to.be.false;
        });

        it('should send error response when dispatchTool throws', async () => {
            dispatchToolStub.throws(new Error('Dispatch failed'));

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('read_files', 'corr-1', '{}'));

            const response = getLastResponse(panel);
            expect(response.success).to.be.false;
            expect(response.error).to.equal('Dispatch failed');
        });
    });

    // -----------------------------------------------------------------------
    // setOnUiReady
    // -----------------------------------------------------------------------

    describe('setOnUiReady', () => {
        it('should set the callback (test via ui_ready action)', async () => {
            const callback = sinon.stub();
            setOnUiReady(callback);

            const panel = createFakePanel();
            await handleVsRequest(panel, createMessage('ui_ready', 'corr-set'));

            expect(callback.calledOnce).to.be.true;
            expect(callback.firstCall.args[0]).to.equal(WORKSPACE);
        });
    });

    // -----------------------------------------------------------------------
    // disposeMcpRegistry
    // -----------------------------------------------------------------------

    describe('disposeMcpRegistry', () => {
        it('should call mcpRegistry.dispose', async () => {
            await disposeMcpRegistry();

            expect(mockMcpInstance.dispose.calledOnce).to.be.true;
        });

        it('should log error if dispose throws', async () => {
            mockMcpInstance.dispose.rejects(new Error('Dispose failed'));

            await disposeMcpRegistry();

            expect(loggerMock.logError.calledOnce).to.be.true;
            expect(loggerMock.logError.firstCall.args[0]).to.include('Dispose failed');
        });
    });

    // -----------------------------------------------------------------------
    // McpClientRegistry instantiation
    // -----------------------------------------------------------------------

    describe('McpClientRegistry instantiation', () => {
        it('should have created McpClientRegistry at module load time', () => {
            expect(MockMcpClientRegistry.calledOnce).to.be.true;
        });
    });
});
