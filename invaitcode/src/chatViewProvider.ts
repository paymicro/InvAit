import * as vscode from 'vscode';
import { setOnUiReady } from './toolDispatcher';
import { registerContextListeners, pushInitialContext, resetContextState, setActivePanel } from './contextPublisher';
import { ensureServer } from './extension';
import { handleWebviewMessage } from './webviewMessageHandler';
import { getWebviewHtml } from './webviewHtml';

/**
 * Provider that renders the Blazor WASM chat UI inside the Activity Bar sidebar.
 * This replaces the old createWebviewPanel approach — the UI now lives in a
 * WebviewView (sidebar) instead of a WebviewPanel (editor tab).
 */
export class ChatViewProvider implements vscode.WebviewViewProvider {

    constructor(
        private readonly context: vscode.ExtensionContext,
    ) {}

    async resolveWebviewView(
        webviewView: vscode.WebviewView,
        _context: vscode.WebviewViewResolveContext,
        _token: vscode.CancellationToken,
    ): Promise<void> {

        // Ensure the static server is running before we load the iframe
        const port = await ensureServer(this.context);

        // Register the view as the active panel for context publishing
        // We need a minimal adapter since WebviewView is not WebviewPanel
        const panelAdapter = {
            webview: webviewView.webview,
            dispose: () => { /* WebviewView has no dispose; lifecycle managed by VS Code */ },
            onDidDispose: webviewView.onDidDispose,
            visible: webviewView.visible,
        } as unknown as vscode.WebviewPanel;

        setActivePanel(panelAdapter);
        registerContextListeners(this.context);
        setOnUiReady((workspaceRoot: string) => pushInitialContext(workspaceRoot));

        webviewView.webview.options = {
            enableScripts: true,
        };

        webviewView.webview.html = getWebviewHtml({ staticServerPort: port });

        // Route incoming messages from Blazor iframe
        webviewView.webview.onDidReceiveMessage(
            async (message: any) => {
                await handleWebviewMessage(message, { panel: panelAdapter, context: this.context });
            },
            undefined,
            this.context.subscriptions,
        );

        webviewView.onDidDispose(() => {
            setActivePanel(null);
            resetContextState();
        }, null, this.context.subscriptions);
    }
}
