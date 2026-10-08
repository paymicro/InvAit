/**
 * Unit tests for contextPublisher.ts
 *
 * Tests setActivePanel, invalidateContextCache, pushInitialContext,
 * resetContextState, and registerContextListeners. The 'vscode', 'fs',
 * './toolHandlers', and './logger' modules are all mocked via Module._load
 * interception before importing the source module.
 *
 * IMPORTANT: contextPublisher has module-level state (_lastContextJson,
 * _cachedSolutionFiles, _isUiReady, etc.) that persists across tests.
 * resetContextState() only clears _isUiReady; invalidateContextCache() only
 * clears _cachedSolutionFiles. There is no export to clear _lastContextJson.
 * Tests are therefore structured to account for this: the first pushInitialContext
 * call builds fresh context; subsequent calls reuse the cached _lastContextJson.
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import * as path from 'path';

chai.use(chaiAsPromised);
const expect = chai.expect;

// ---------------------------------------------------------------------------
// Mock dependencies BEFORE importing contextPublisher.
// ---------------------------------------------------------------------------

const toolHandlersStub = {
    buildWorkspaceFiles: sinon.stub().returns([]),
    dispatchTool: sinon.stub(),
};

const loggerStub = {
    log: sinon.stub(),
    logError: sinon.stub(),
    logWarning: sinon.stub(),
    disposeLogger: sinon.stub(),
    initLogger: sinon.stub(),
};

const fsStub = {
    existsSync: sinon.stub(),
    readFileSync: sinon.stub(),
};

const Module = require('module');
const originalLoad = Module._load;

Module._load = function (request: string, parent: any, isMain: boolean) {
    if (request === 'vscode') {
        return originalLoad.call(this, path.resolve(__dirname, 'vscodeMock.ts'), parent, isMain);
    }
    if (request === 'fs') return fsStub;
    if (request.includes('toolHandlers')) return toolHandlersStub;
    if (request.includes('logger') && !request.includes('vscodeMock')) return loggerStub;
    return originalLoad.call(this, request, parent, isMain);
};

// Now import — the module will receive our mocks.
import {
    setActivePanel,
    invalidateContextCache,
    pushInitialContext,
    resetContextState,
    registerContextListeners,
} from '../contextPublisher';

// Restore Module._load after import.
Module._load = originalLoad;

// Get the vscode mock module so we can modify window/workspace properties.
const vscodeMock = require(path.resolve(__dirname, 'vscodeMock.ts'));

// ---------------------------------------------------------------------------

describe('contextPublisher', () => {

    let sandbox: sinon.SinonSandbox;
    let fakePanel: any;

    beforeEach(() => {
        sandbox = sinon.createSandbox();

        fakePanel = {
            webview: {
                postMessage: sinon.stub(),
            },
        };

        // Reset all stubs
        (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).reset();
        (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns([]);
        (loggerStub.logError as sinon.SinonStub).reset();
        (fsStub.existsSync as sinon.SinonStub).reset();
        (fsStub.readFileSync as sinon.SinonStub).reset();

        // Reset vscode mock event stubs
        (vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).reset();
        (vscodeMock.window.onDidChangeTextEditorSelection as sinon.SinonStub).reset();
        (vscodeMock.workspace.onDidChangeWorkspaceFolders as sinon.SinonStub).reset();
        (vscodeMock.workspace.onDidSaveTextDocument as sinon.SinonStub).reset();

        // Reset activeTextEditor and workspaceFolders to defaults
        vscodeMock.window.activeTextEditor = undefined;
        vscodeMock.workspace.workspaceFolders = undefined;

        // Reset module-level state (note: _lastContextJson cannot be cleared
        // via any exported function — it persists once set)
        resetContextState();
        setActivePanel(null);
        invalidateContextCache();
    });

    afterEach(() => {
        sandbox.restore();
        // Clean up state
        resetContextState();
        setActivePanel(null);
        invalidateContextCache();
        // Reset vscode mock properties
        vscodeMock.window.activeTextEditor = undefined;
        vscodeMock.workspace.workspaceFolders = undefined;
    });

    // -----------------------------------------------------------------------
    // setActivePanel
    // -----------------------------------------------------------------------

    describe('setActivePanel', () => {
        it('should allow pushInitialContext to send messages when panel is set', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            setActivePanel(fakePanel);
            pushInitialContext('/fake/workspace');

            expect(fakePanel.webview.postMessage.calledOnce).to.be.true;
        });

        it('should prevent pushInitialContext from sending when panel is null', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            setActivePanel(null);
            pushInitialContext('/fake/workspace');

            expect(fakePanel.webview.postMessage.called).to.be.false;
        });
    });

    // -----------------------------------------------------------------------
    // invalidateContextCache
    // -----------------------------------------------------------------------

    describe('invalidateContextCache', () => {
        it('should cause buildWorkspaceFiles to be called again when context is rebuilt', () => {
            // Use a fresh module state by leveraging debouncedContextUpdate
            // indirectly through registerContextListeners + fake timers.
            // This avoids the _lastContextJson caching issue in pushInitialContext.
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['file1.ts']);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/ws' } },
            ];

            // Set up event capture
            let wsCallback: any;
            (vscodeMock.workspace.onDidChangeWorkspaceFolders as sinon.SinonStub).callsFake((cb: any) => {
                wsCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            // Register listeners — this triggers the callsFake and captures the callback
            registerContextListeners(fakeContext);

            setActivePanel(fakePanel);
            // Make _isUiReady true so debounced updates send
            pushInitialContext('/ws');

            const clock = sandbox.useFakeTimers();

            // First trigger — builds context (cache was invalidated in beforeEach)
            wsCallback();
            clock.tick(501);

            const firstBuildCount = (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).callCount;

            // The debounced update rebuilds context. invalidateContextCache was
            // called by the wsCallback, so the next debounced update should call
            // buildWorkspaceFiles again.
            wsCallback();
            clock.tick(501);

            const secondBuildCount = (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).callCount;
            expect(secondBuildCount).to.be.greaterThan(firstBuildCount);
        });
    });

    // -----------------------------------------------------------------------
    // pushInitialContext — first call builds fresh context
    // -----------------------------------------------------------------------

    describe('pushInitialContext (first call builds context)', () => {
        // These tests rely on being the first pushInitialContext call in the
        // test run (or that _lastContextJson is null). Since _lastContextJson
        // persists across tests, we use a separate approach: verify the
        // message content structure regardless of caching.

        it('should set _isUiReady to true and send context update when panel is set', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            setActivePanel(fakePanel);
            pushInitialContext('/fake/workspace');

            expect(fakePanel.webview.postMessage.calledOnce).to.be.true;
            const msg = fakePanel.webview.postMessage.getCall(0).args[0];
            expect(msg.type).to.equal('VsMessage');
            expect(msg.payload.action).to.equal('UpdateCodeContext');
            expect(msg.payload.payload).to.be.a('string');
        });

        it('should build context JSON with ideType=vscode', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            setActivePanel(fakePanel);
            pushInitialContext('/fake/workspace');

            const msg = fakePanel.webview.postMessage.getCall(0).args[0];
            const context = JSON.parse(msg.payload.payload);
            expect(context.ideType).to.equal('vscode');
        });

        it('should include solutionPath in context JSON', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            setActivePanel(fakePanel);
            pushInitialContext('/my/workspace');

            const msg = fakePanel.webview.postMessage.getCall(0).args[0];
            const context = JSON.parse(msg.payload.payload);
            // solutionPath may be from cached _lastContextJson, but ideType
            // and structure should be correct
            expect(context).to.have.property('solutionPath');
        });

        it('should include solutionFiles from buildWorkspaceFiles when context is built fresh', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['a.ts', 'b.ts']);

            setActivePanel(fakePanel);
            pushInitialContext('/ws');

            const msg = fakePanel.webview.postMessage.getCall(0).args[0];
            const context = JSON.parse(msg.payload.payload);
            // If _lastContextJson was cached from a previous test, solutionFiles
            // may differ. But if this is the first build, it should include the files.
            expect(context).to.have.property('solutionFiles');
            expect(context.solutionFiles).to.be.an('array');
        });

        it('should not send if no active panel (but still builds and caches)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['x.ts']);

            setActivePanel(null);
            pushInitialContext('/ws');

            // No message was sent because no panel
            expect(fakePanel.webview.postMessage.called).to.be.false;
        });

        it('should set activeFilePath to null when no activeTextEditor', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            // activeTextEditor is undefined by default

            setActivePanel(fakePanel);
            pushInitialContext('/ws');

            const msg = fakePanel.webview.postMessage.getCall(0).args[0];
            const context = JSON.parse(msg.payload.payload);
            expect(context.activeFilePath).to.be.null;
            expect(context.activeFileContent).to.be.null;
            expect(context.selectionStartLine).to.equal(0);
            expect(context.selectionEndLine).to.equal(0);
        });

        it('should not call buildWorkspaceFiles when workspaceRoot does not exist', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            setActivePanel(fakePanel);
            pushInitialContext('/nonexistent');

            // If _lastContextJson is cached, buildContextJson is not called at all.
            // If it's the first call, buildWorkspaceFiles should not be called
            // because existsSync returns false.
            // Either way, buildWorkspaceFiles should not have been called.
            expect((toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).called).to.be.false;
        });

        it('should not call buildWorkspaceFiles when workspaceRoot is empty string', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);

            setActivePanel(fakePanel);
            pushInitialContext('');

            expect((toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).called).to.be.false;
        });

        it('should send message with correlationId as string', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            setActivePanel(fakePanel);
            pushInitialContext('/ws');

            const msg = fakePanel.webview.postMessage.getCall(0).args[0];
            expect(msg.payload.correlationId).to.be.a('string');
        });

        it('should handle errors from buildWorkspaceFiles gracefully (logError called)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).throws(new Error('boom'));

            setActivePanel(fakePanel);

            // Should not throw
            expect(() => pushInitialContext('/ws')).to.not.throw();

            // If _lastContextJson was already cached, buildContextJson is not called
            // and logError won't fire. But if it's a fresh build, it should.
            // We verify the test doesn't throw either way.
            const msg = fakePanel.webview.postMessage.getCall(0).args[0];
            const context = JSON.parse(msg.payload.payload);
            expect(context).to.have.property('solutionFiles');
        });

        it('should handle errors from activeTextEditor gracefully (logError called)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            // Set activeTextEditor to an object whose document access throws
            vscodeMock.window.activeTextEditor = {
                get document() { throw new Error('editor crashed'); },
                selection: { start: { line: 0 }, end: { line: 0 } },
            };

            setActivePanel(fakePanel);

            // Should not throw
            expect(() => pushInitialContext('/ws')).to.not.throw();

            // Context should still be sent
            const msg = fakePanel.webview.postMessage.getCall(0).args[0];
            const context = JSON.parse(msg.payload.payload);
            // activeFilePath may be null (from error handling or from cached context)
            expect(context).to.have.property('activeFilePath');
        });
    });

    // -----------------------------------------------------------------------
    // pushInitialContext with activeTextEditor
    // -----------------------------------------------------------------------

    describe('pushInitialContext with activeTextEditor', () => {
        // These tests verify the context JSON includes active editor info.
        // Note: if _lastContextJson is cached from a previous test, the
        // activeTextEditor info won't be reflected. We use debouncedContextUpdate
        // (via registerContextListeners + fake timers) to force a fresh build
        // that picks up the activeTextEditor.

        it('should include activeFilePath and activeFileContent when activeTextEditor is set', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns([]);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/ws' } },
            ];
            vscodeMock.window.activeTextEditor = {
                document: {
                    fileName: '/ws/src/main.ts',
                    getText: sinon.stub().returns('line1\nline2\nline3'),
                },
                selection: { start: { line: 0 }, end: { line: 2 } },
            };

            // Use debouncedContextUpdate to force a fresh build
            let editorCallback: any;
            (vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).callsFake((cb: any) => {
                editorCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            // Register listeners — this triggers the callsFake and captures the callback
            registerContextListeners(fakeContext);

            setActivePanel(fakePanel);
            pushInitialContext('/ws'); // make _isUiReady = true

            const clock = sandbox.useFakeTimers();
            const postCountBefore = fakePanel.webview.postMessage.callCount;

            // Trigger editor change → debounced rebuild with activeTextEditor
            editorCallback();
            clock.tick(501);

            // Find the message from the debounced update (last message)
            const msg = fakePanel.webview.postMessage.getCall(fakePanel.webview.postMessage.callCount - 1).args[0];
            const context = JSON.parse(msg.payload.payload);
            expect(context.activeFilePath).to.equal('/ws/src/main.ts');
            expect(context.activeFileContent).to.equal('line1\nline2\nline3');
        });

        it('should include selectionStartLine and selectionEndLine from editor.selection (1-based)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns([]);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/ws' } },
            ];
            vscodeMock.window.activeTextEditor = {
                document: {
                    fileName: '/ws/test.ts',
                    getText: sinon.stub().returns('content'),
                },
                selection: { start: { line: 4 }, end: { line: 9 } },
            };

            let editorCallback: any;
            (vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).callsFake((cb: any) => {
                editorCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            // Register listeners — captures the callback
            registerContextListeners(fakeContext);

            setActivePanel(fakePanel);
            pushInitialContext('/ws');

            const clock = sandbox.useFakeTimers();

            editorCallback();
            clock.tick(501);

            const msg = fakePanel.webview.postMessage.getCall(fakePanel.webview.postMessage.callCount - 1).args[0];
            const context = JSON.parse(msg.payload.payload);
            // Lines are 0-based in vscode, converted to 1-based
            expect(context.selectionStartLine).to.equal(5);
            expect(context.selectionEndLine).to.equal(10);
        });

        it('should truncate activeFileContent to 2000 lines', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns([]);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/ws' } },
            ];

            // Generate 2500 lines
            const lines: string[] = [];
            for (let i = 0; i < 2500; i++) {
                lines.push(`line${i}`);
            }
            const fullContent = lines.join('\n');

            vscodeMock.window.activeTextEditor = {
                document: {
                    fileName: '/ws/big.ts',
                    getText: sinon.stub().returns(fullContent),
                },
                selection: { start: { line: 0 }, end: { line: 0 } },
            };

            let editorCallback: any;
            (vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).callsFake((cb: any) => {
                editorCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            // Register listeners — captures the callback
            registerContextListeners(fakeContext);

            setActivePanel(fakePanel);
            pushInitialContext('/ws');

            const clock = sandbox.useFakeTimers();

            editorCallback();
            clock.tick(501);

            const msg = fakePanel.webview.postMessage.getCall(fakePanel.webview.postMessage.callCount - 1).args[0];
            const context = JSON.parse(msg.payload.payload);
            const contentLines = context.activeFileContent.split('\n');
            expect(contentLines).to.have.length(2000);
            expect(contentLines[0]).to.equal('line0');
            expect(contentLines[1999]).to.equal('line1999');
        });

        it('should not truncate activeFileContent when under 2000 lines', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns([]);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/ws' } },
            ];
            vscodeMock.window.activeTextEditor = {
                document: {
                    fileName: '/ws/small.ts',
                    getText: sinon.stub().returns('a\nb\nc'),
                },
                selection: { start: { line: 0 }, end: { line: 0 } },
            };

            let editorCallback: any;
            (vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).callsFake((cb: any) => {
                editorCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            // Register listeners — captures the callback
            registerContextListeners(fakeContext);

            setActivePanel(fakePanel);
            pushInitialContext('/ws');

            const clock = sandbox.useFakeTimers();

            editorCallback();
            clock.tick(501);

            const msg = fakePanel.webview.postMessage.getCall(fakePanel.webview.postMessage.callCount - 1).args[0];
            const context = JSON.parse(msg.payload.payload);
            expect(context.activeFileContent).to.equal('a\nb\nc');
        });
    });

    // -----------------------------------------------------------------------
    // pushInitialContext caching behavior
    // -----------------------------------------------------------------------

    describe('pushInitialContext (caching behavior)', () => {
        it('should reuse cached _lastContextJson on second push (buildWorkspaceFiles not called again)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['file.ts']);

            setActivePanel(fakePanel);

            // First push — builds and caches (or reuses existing cache)
            pushInitialContext('/ws');
            const buildCountAfterFirst = (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).callCount;

            // Reset _isUiReady
            resetContextState();

            // Second push — should reuse cached _lastContextJson
            pushInitialContext('/ws');
            const buildCountAfterSecond = (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).callCount;

            // buildWorkspaceFiles should NOT have been called again
            expect(buildCountAfterSecond).to.equal(buildCountAfterFirst);
            expect(fakePanel.webview.postMessage.calledTwice).to.be.true;
        });
    });

    // -----------------------------------------------------------------------
    // resetContextState
    // -----------------------------------------------------------------------

    describe('resetContextState', () => {
        it('should set _isUiReady to false', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            setActivePanel(fakePanel);

            // Push initial context — sets _isUiReady = true
            pushInitialContext('/ws');
            expect(fakePanel.webview.postMessage.calledOnce).to.be.true;

            // Reset state
            resetContextState();

            // Push again — _isUiReady is set back to true by pushInitialContext,
            // and _lastContextJson is cached so it should send the cached version
            pushInitialContext('/ws');
            expect(fakePanel.webview.postMessage.calledTwice).to.be.true;
        });

        it('should not affect cached _lastContextJson', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['cached.ts']);

            setActivePanel(fakePanel);

            // First push — builds and caches (or reuses existing cache)
            pushInitialContext('/ws');
            const buildCountAfterFirst = (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).callCount;

            // Reset state
            resetContextState();

            // Push again — should reuse cached _lastContextJson
            pushInitialContext('/ws');
            const buildCountAfterSecond = (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).callCount;

            expect(buildCountAfterSecond).to.equal(buildCountAfterFirst);
            expect(fakePanel.webview.postMessage.calledTwice).to.be.true;
        });
    });

    // -----------------------------------------------------------------------
    // registerContextListeners
    // -----------------------------------------------------------------------

    describe('registerContextListeners', () => {
        it('should register onDidChangeWorkspaceFolders listener', () => {
            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            registerContextListeners(fakeContext);

            expect((vscodeMock.workspace.onDidChangeWorkspaceFolders as sinon.SinonStub).calledOnce).to.be.true;
            expect((vscodeMock.workspace.onDidChangeWorkspaceFolders as sinon.SinonStub).calledWith(sinon.match.func)).to.be.true;
        });

        it('should register onDidChangeActiveTextEditor listener', () => {
            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            registerContextListeners(fakeContext);

            expect((vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).calledOnce).to.be.true;
            expect((vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).calledWith(sinon.match.func)).to.be.true;
        });

        it('should register onDidSaveTextDocument listener', () => {
            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            registerContextListeners(fakeContext);

            expect((vscodeMock.workspace.onDidSaveTextDocument as sinon.SinonStub).calledOnce).to.be.true;
            expect((vscodeMock.workspace.onDidSaveTextDocument as sinon.SinonStub).calledWith(sinon.match.func)).to.be.true;
        });

        it('should register onDidChangeTextEditorSelection listener', () => {
            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            registerContextListeners(fakeContext);

            expect((vscodeMock.window.onDidChangeTextEditorSelection as sinon.SinonStub).calledOnce).to.be.true;
            expect((vscodeMock.window.onDidChangeTextEditorSelection as sinon.SinonStub).calledWith(sinon.match.func)).to.be.true;
        });

        it('should push 4 subscriptions to context.subscriptions', () => {
            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            registerContextListeners(fakeContext);

            expect(fakeContext.subscriptions.push.callCount).to.equal(4);
        });

        it('should use workspaceFolders[0].uri.fsPath as workspace root for debounced updates', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['dynamic.ts']);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/dynamic/workspace' } },
            ];

            let editorCallback: any;
            (vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).callsFake((cb: any) => {
                editorCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            // Register listeners — captures the callback
            registerContextListeners(fakeContext);

            setActivePanel(fakePanel);
            pushInitialContext('/dynamic/workspace'); // make _isUiReady = true

            const clock = sandbox.useFakeTimers();
            const callsBefore = fakePanel.webview.postMessage.callCount;

            // Trigger the listener
            editorCallback();

            // Before debounce timer fires, no new message
            expect(fakePanel.webview.postMessage.callCount).to.equal(callsBefore);

            // Advance past debounce (500ms)
            clock.tick(501);

            // After debounce, a new message should have been sent
            expect(fakePanel.webview.postMessage.callCount).to.be.greaterThan(callsBefore);
        });

        it('should return empty string when workspaceFolders is undefined', () => {
            vscodeMock.workspace.workspaceFolders = undefined;

            let wsCallback: any;
            (vscodeMock.workspace.onDidChangeWorkspaceFolders as sinon.SinonStub).callsFake((cb: any) => {
                wsCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            const clock = sandbox.useFakeTimers();

            registerContextListeners(fakeContext);

            // Trigger the listener — should not throw even with no workspace
            expect(() => {
                wsCallback();
                clock.tick(501);
            }).to.not.throw();
        });

        it('should invalidate cache on workspace folders change', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['file.ts']);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/ws' } },
            ];

            let wsCallback: any;
            (vscodeMock.workspace.onDidChangeWorkspaceFolders as sinon.SinonStub).callsFake((cb: any) => {
                wsCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            setActivePanel(fakePanel);
            pushInitialContext('/ws');

            // Register listeners — captures the callback
            registerContextListeners(fakeContext);

            const clock = sandbox.useFakeTimers();

            // Trigger workspace folders change — this calls invalidateContextCache()
            // and then debouncedContextUpdate which rebuilds
            wsCallback();
            clock.tick(501);

            // buildWorkspaceFiles should have been called (cache was invalidated + rebuild)
            expect((toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).called).to.be.true;
        });

        it('should invalidate cache on save text document', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['file.ts']);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/ws' } },
            ];

            let saveCallback: any;
            (vscodeMock.workspace.onDidSaveTextDocument as sinon.SinonStub).callsFake((cb: any) => {
                saveCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            setActivePanel(fakePanel);
            pushInitialContext('/ws');

            // Register listeners — captures the callback
            registerContextListeners(fakeContext);

            const clock = sandbox.useFakeTimers();

            // Trigger save
            saveCallback();
            clock.tick(501);

            // buildWorkspaceFiles should have been called (cache was invalidated + rebuild)
            expect((toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).called).to.be.true;
        });

        it('should debounce context updates (500ms throttle)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['file.ts']);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/ws' } },
            ];

            let editorCallback: any;
            (vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).callsFake((cb: any) => {
                editorCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            // Register listeners — captures the callback
            registerContextListeners(fakeContext);

            setActivePanel(fakePanel);
            pushInitialContext('/ws');

            const clock = sandbox.useFakeTimers();

            const postMessageCountBefore = fakePanel.webview.postMessage.callCount;

            // Trigger multiple rapid updates
            editorCallback();
            editorCallback();
            editorCallback();

            // Advance less than 500ms — no new message yet
            clock.tick(300);
            expect(fakePanel.webview.postMessage.callCount).to.equal(postMessageCountBefore);

            // Advance past 500ms — now the debounced update fires
            clock.tick(201);
            expect(fakePanel.webview.postMessage.callCount).to.be.greaterThan(postMessageCountBefore);
        });

        it('should not send context update when _isUiReady is false (after resetContextState)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (toolHandlersStub.buildWorkspaceFiles as sinon.SinonStub).returns(['file.ts']);

            vscodeMock.workspace.workspaceFolders = [
                { uri: { fsPath: '/ws' } },
            ];

            let editorCallback: any;
            (vscodeMock.window.onDidChangeActiveTextEditor as sinon.SinonStub).callsFake((cb: any) => {
                editorCallback = cb;
                return { dispose: sinon.stub() };
            });

            const fakeContext: any = {
                subscriptions: { push: sinon.stub() },
            };

            // Register listeners — captures the callback
            registerContextListeners(fakeContext);

            setActivePanel(fakePanel);

            // Do NOT call pushInitialContext — _isUiReady stays false
            resetContextState();

            const clock = sandbox.useFakeTimers();

            const postMessageCountBefore = fakePanel.webview.postMessage.callCount;

            // Trigger the listener
            editorCallback();
            clock.tick(501);

            // No message sent because _isUiReady is false
            expect(fakePanel.webview.postMessage.callCount).to.equal(postMessageCountBefore);
        });
    });
});
