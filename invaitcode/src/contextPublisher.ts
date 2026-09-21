import * as vscode from 'vscode';
import * as fs from 'fs';
import { buildWorkspaceTree } from './toolHandlers';
import { log, logError } from './logger';

const CONTEXT_THROTTLE_MS = 500;

// State
let _isUiReady = false;
let _lastContextJson: string | null = null;
let _contextUpdateTimer: ReturnType<typeof setTimeout> | null = null;
let _activePanel: vscode.WebviewPanel | null = null;
let _cachedSolutionFiles: string[] | null = null;
let _cachedWorkspaceRoot: string = '';

export function setActivePanel(panel: vscode.WebviewPanel | null): void {
    _activePanel = panel;
}

export function invalidateContextCache(): void {
    _cachedSolutionFiles = null;
}

/**
 * Builds the VsCodeContext JSON from the current workspace state.
 */
function buildContextJson(workspaceRoot: string): string {
    const context: any = {
        ideType: 'vscode',
        solutionPath: workspaceRoot,
        solutionFiles: [] as string[],
        activeFilePath: null as string | null,
        activeFileContent: null as string | null,
        selectionStartLine: 0,
        selectionEndLine: 0
    };

    // 1. Workspace structure (cached — only rebuild on structural changes)
    try {
        if (workspaceRoot && fs.existsSync(workspaceRoot)) {
            if (_cachedSolutionFiles === null || workspaceRoot !== _cachedWorkspaceRoot) {
                _cachedSolutionFiles = buildWorkspaceTree(workspaceRoot);
                _cachedWorkspaceRoot = workspaceRoot;
            }
            context.solutionFiles = _cachedSolutionFiles;
        }
    } catch (e: any) {
        logError('[InvAit Ext] Error building workspace tree: ' + e.message);
    }

    // 2. Active document
    try {
        const editor = vscode.window.activeTextEditor;
        if (editor && editor.document) {
            context.activeFilePath = editor.document.fileName;
            const content = editor.document.getText();
            // Truncate to ~2000 lines to match VS behavior
            const lines = content.split(/\r\n|\r|\n/);
            if (lines.length > 2000) {
                context.activeFileContent = lines.slice(0, 2000).join('\n');
            } else {
                context.activeFileContent = content;
            }
            const sel = editor.selection;
            context.selectionStartLine = sel.start.line + 1;
            context.selectionEndLine = sel.end.line + 1;
        }
    } catch (e: any) {
        logError('[InvAit Ext] Error getting active document: ' + e.message);
    }

    return JSON.stringify(context);
}

function sendContextUpdate(contextJson: string): void {
    if (!_activePanel) return;
    _activePanel.webview.postMessage({
        type: 'VsMessage',
        payload: {
            action: 'UpdateCodeContext',
            correlationId: Date.now().toString(),
            payload: contextJson
        }
    });
}

function debouncedContextUpdate(workspaceRoot: string): void {
    if (_contextUpdateTimer) clearTimeout(_contextUpdateTimer);
    _contextUpdateTimer = setTimeout(() => {
        _contextUpdateTimer = null;
        const contextJson = buildContextJson(workspaceRoot);
        _lastContextJson = contextJson;
        if (_isUiReady) {
            sendContextUpdate(contextJson);
        }
    }, CONTEXT_THROTTLE_MS);
}

export function pushInitialContext(workspaceRoot: string): void {
    _isUiReady = true;
    if (_lastContextJson) {
        sendContextUpdate(_lastContextJson);
    } else {
        const contextJson = buildContextJson(workspaceRoot);
        _lastContextJson = contextJson;
        sendContextUpdate(contextJson);
    }
}

export function resetContextState(): void {
    _isUiReady = false;
}

/**
 * Registers workspace/editor listeners that trigger context updates.
 * Reads current workspace root dynamically (supports folder changes).
 */
export function registerContextListeners(context: vscode.ExtensionContext): void {
    const getRoot = () => vscode.workspace.workspaceFolders?.[0]?.uri.fsPath || '';

    context.subscriptions.push(
        vscode.workspace.onDidChangeWorkspaceFolders(() => {
            invalidateContextCache();
            debouncedContextUpdate(getRoot());
        })
    );

    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(() => {
            debouncedContextUpdate(getRoot());
        })
    );

    context.subscriptions.push(
        vscode.workspace.onDidSaveTextDocument(() => {
            invalidateContextCache();
            debouncedContextUpdate(getRoot());
        })
    );

    context.subscriptions.push(
        vscode.window.onDidChangeTextEditorSelection(() => {
            debouncedContextUpdate(getRoot());
        })
    );
}
