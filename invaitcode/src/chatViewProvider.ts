import * as vscode from 'vscode';
import { handleVsRequest, setOnUiReady } from './toolDispatcher';
import { registerContextListeners, pushInitialContext, resetContextState, setActivePanel } from './contextPublisher';
import { handleNetworkProxyRequest, setSkipSslValidation } from './networkProxy';
import { ensureServer } from './extension';
import { log, logError } from './logger';

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
            dispose: () => webviewView.dispose(),
            onDidDispose: webviewView.onDidDispose,
            visible: webviewView.visible,
        } as unknown as vscode.WebviewPanel;

        setActivePanel(panelAdapter);
        registerContextListeners(this.context);
        setOnUiReady((workspaceRoot: string) => pushInitialContext(workspaceRoot));

        webviewView.webview.options = {
            enableScripts: true,
        };

        webviewView.webview.html = this.getWebviewHtml(port);

        // Route incoming messages from Blazor iframe
        webviewView.webview.onDidReceiveMessage(
            async (message: any) => {
                // Network proxy requests
                if (message.command === 'NetworkProxyRequest') {
                    await handleNetworkProxyRequest(panelAdapter, message.payload);
                    return;
                }

                // Storage persistence: proxy localStorage writes to globalState
                if (message.command === 'StorageSet') {
                    await this.context.globalState.update(message.payload.key, message.payload.value);
                    return;
                }
                if (message.command === 'StorageRemove') {
                    await this.context.globalState.update(message.payload.key, undefined);
                    return;
                }

                // Skip SSL validation toggle (no correlationId — must be before VsRequest check)
                if (message.action === 'skip_ssl_validation') {
                    const skip = message.payload === 'True' || message.payload === 'true';
                    setSkipSslValidation(skip);
                    return;
                }

                // Tool requests (VsRequest)
                if (message.action && message.correlationId) {
                    handleVsRequest(panelAdapter, message);
                    return;
                }
            },
            undefined,
            this.context.subscriptions,
        );

        webviewView.onDidDispose(() => {
            setActivePanel(null);
            resetContextState();
        }, null, this.context.subscriptions);
    }

    private getWebviewHtml(port: number): string {
        return `
            <!DOCTYPE html>
            <html lang="ru">
            <head>
                <meta charset="UTF-8">
                <style>
                    body, iframe { margin: 0; padding: 0; width: 100%; height: 100vh; border: none; overflow: hidden; }
                </style>
            </head>
            <body>
                <iframe id="blazor-frame" src="http://127.0.0.1:${port}/index.html"></iframe>
                <script>
                    const vscode = acquireVsCodeApi();
                    const iframe = document.getElementById('blazor-frame');

                    window.addEventListener('message', (event) => {
                        // Message FROM Blazor iframe → forward to VS Code extension host
                        if (event.data && event.data.target === 'vscode-webview') {
                            vscode.postMessage(event.data.packet);
                            return;
                        }
                        // Message FROM VS Code extension host → forward into Blazor iframe
                        if (iframe && iframe.contentWindow) {
                            iframe.contentWindow.postMessage({
                                source: 'vscode-parent',
                                message: event.data
                            }, '*');
                        }
                    });
                </script>
            </body>
            </html>
        `;
    }
}
