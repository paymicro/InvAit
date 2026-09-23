/**
 * Minimal vscode API mock.
 *
 * This file is registered as the 'vscode' module via ts-node's
 * `require` hooks in .mocharc.json so that any `import * as vscode
 * from 'vscode'` resolves to this mock instead of the real extension.
 */
import * as sinon from 'sinon';


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

export const window = {
    createOutputChannel: sinon.stub().returns(mockOutputChannel),
    activeTextEditor: undefined as any,
    onDidChangeActiveTextEditor: sinon.stub(),
    onDidChangeTextEditorSelection: sinon.stub(),
};

export const workspace = {
    workspaceFolders: undefined as any,
    onDidChangeWorkspaceFolders: sinon.stub(),
    onDidSaveTextDocument: sinon.stub(),
    fs: {},
};

export const commands = { registerCommand: sinon.stub() };

export const Uri = { file: (p: string) => ({ fsPath: p }) };

export class WebviewPanel {}
export class ExtensionContext {}
export class OutputChannel {}

export { mockOutputChannel, mockGlobalState, mockExtensionContext, mockTextEditor };
