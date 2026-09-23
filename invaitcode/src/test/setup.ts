/**
 * Test setup — runs before all test files.
 *
 * Provides a minimal `vscode` module mock so that modules importing
 * 'vscode' (logger, staticServer, networkProxy, contextPublisher) can
 * be loaded without the real VS Code runtime.
 */
import * as sinon from 'sinon';

// ---------------------------------------------------------------------------
// Minimal vscode API mock
// ---------------------------------------------------------------------------

const mockOutputChannel = {
    appendLine: sinon.stub(),
    show: sinon.stub(),
    dispose: sinon.stub(),
};

const mockGlobalState = {
    get: sinon.stub().returns(undefined),
    keys: sinon.stub().returns([]),
    update: sinon.stub().resolves(),
};

const mockMemento = {
    get: sinon.stub().returns(undefined),
    keys: sinon.stub().returns([]),
    update: sinon.stub().resolves(),
};

const mockExtensionContext: any = {
    globalState: mockGlobalState,
    workspaceState: mockMemento,
    secrets: { get: sinon.stub(), store: sinon.stub(), delete: sinon.stub() },
    subscriptions: { push: sinon.stub() },
    extensionPath: '/fake/extension/path',
    globalStoragePath: '/fake/global/storage',
    asAbsolutePath: (p: string) => '/fake/extension/' + p,
};

const mockTextEditor = {
    document: {
        fileName: '',
        getText: sinon.stub().returns(''),
    },
    selection: { start: { line: 0 }, end: { line: 0 } },
};

const vscodeMock = {
    window: {
        createOutputChannel: sinon.stub().returns(mockOutputChannel),
        activeTextEditor: undefined as any,
        onDidChangeActiveTextEditor: sinon.stub(),
        onDidChangeTextEditorSelection: sinon.stub(),
    },
    workspace: {
        workspaceFolders: undefined as any,
        onDidChangeWorkspaceFolders: sinon.stub(),
        onDidSaveTextDocument: sinon.stub(),
        fs: {},
    },
    commands: { registerCommand: sinon.stub() },
    WebviewPanel: class { },
    ExtensionContext: class { },
    OutputChannel: class { },
    Uri: { file: (p: string) => ({ fsPath: p }) },
};

// Register the mock in the module system BEFORE any test imports.
const Module = require('module');
const originalResolve = (Module as any)._resolveFilename;
(Module as any)._resolveFilename = function (request: string, ...args: any[]) {
    if (request === 'vscode') {
        return require.resolve('./vscodeMock');
    }
    return originalResolve.call(this, request, ...args);
};

// Export the mock as a module so _resolveFilename can find it.
// We use a trick: write the mock to a temp module path.
const vscodeMockModule = {
    __esModule: true,
    ...vscodeMock,
    default: vscodeMock,
};

// Actually, simpler approach: use Module._cache directly.
const vscodePath = require.resolve('./vscodeMock');
(Module as any)._cache[vscodePath] = {
    id: vscodePath,
    filename: vscodePath,
    loaded: true,
    exports: vscodeMockModule,
};

// Re-export for test files that need direct access.
export { vscodeMock, mockOutputChannel, mockGlobalState, mockExtensionContext, mockTextEditor };
