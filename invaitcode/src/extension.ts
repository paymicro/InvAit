import * as vscode from 'vscode';
import * as path from 'path';
import { createStaticServer } from './staticServer';
import { handleVsRequest, setOnUiReady, disposeMcpRegistry } from './toolDispatcher';
import { registerContextListeners, pushInitialContext, resetContextState, setActivePanel } from './contextPublisher';
import { handleNetworkProxyRequest, setSkipSslValidation } from './networkProxy';
import { ChatViewProvider } from './chatViewProvider';
import { initLogger, disposeLogger, log } from './logger';

let server: import('http').Server | null = null;
let currentPort = 0;

export function activate(context: vscode.ExtensionContext) {
    initLogger();

    // --- Sidebar view (Activity Bar icon) ---
    const chatViewProvider = new ChatViewProvider(context);
    context.subscriptions.push(
        vscode.window.registerWebviewViewProvider('invaitcode.chatView', chatViewProvider, {
            webviewOptions: { retainContextWhenHidden: true },
        })
    );

    // --- Command: open chat (also focuses the sidebar) ---
    let disposable = vscode.commands.registerCommand('invaitcode.chat', async () => {
        // Focus the sidebar view
        await vscode.commands.executeCommand('workbench.view.extension.invaitcode-sidebar');
        await vscode.commands.executeCommand('invaitcode.chatView.focus');
    });

    context.subscriptions.push(disposable);

    // --- Command: open as editor tab (legacy fallback) ---
    let editorCmd = vscode.commands.registerCommand('invaitcode.view', async () => {
        const wwwrootPath = path.join(context.extensionPath, 'wwwroot');

        if (!server) {
            server = await createStaticServer(wwwrootPath, context);
            const address = server.address();
            if (address && typeof address !== 'string') {
                log(`Blazor WASM Static Server running on http://127.0.0.1:${address.port}`);
                currentPort = address.port;
                createWebviewEditor(address.port, context);
            }
        } else {
            createWebviewEditor(currentPort, context);
        }
    });

    context.subscriptions.push(editorCmd);
}

/**
 * Ensure the static server is running and return its port.
 * Called by ChatViewProvider when the sidebar view is resolved.
 */
export async function ensureServer(context: vscode.ExtensionContext): Promise<number> {
    if (!server) {
        const wwwrootPath = path.join(context.extensionPath, 'wwwroot');
        server = await createStaticServer(wwwrootPath, context);
        const address = server.address();
        if (address && typeof address !== 'string') {
            currentPort = address.port;
            log(`Blazor WASM Static Server running on http://127.0.0.1:${currentPort}`);
        }
    }
    return currentPort;
}

export async function deactivate() {
    // Stop all MCP servers and dispose the registry
    await disposeMcpRegistry();

    if (server) {
        server.close();
        server = null;
    }

    disposeLogger();
}

function createWebviewEditor(port: number, context: vscode.ExtensionContext) {
    const panel = vscode.window.createWebviewPanel(
        'blazorView',
        'InvAit Chat',
        vscode.ViewColumn.One,
        {
            enableScripts: true,
            retainContextWhenHidden: true
        }
    );

    panel.webview.html = getWebviewHtml(port);

    // Wire up context publisher
    setActivePanel(panel);
    registerContextListeners(context);
    setOnUiReady((workspaceRoot) => pushInitialContext(workspaceRoot));

    panel.onDidDispose(() => {
        setActivePanel(null);
        resetContextState();
    }, null, context.subscriptions);

    // Route incoming messages from Blazor iframe
    panel.webview.onDidReceiveMessage(
        async (message: any) => {
            // Network proxy requests (from DynamicEnvironmentHttpMessageHandler)
            if (message.command === 'NetworkProxyRequest') {
                await handleNetworkProxyRequest(panel, message.payload);
                return;
            }

            // Storage persistence: proxy localStorage writes to globalState
            if (message.command === 'StorageSet') {
                await context.globalState.update(message.payload.key, message.payload.value);
                return;
            }
            if (message.command === 'StorageRemove') {
                await context.globalState.update(message.payload.key, undefined);
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
                handleVsRequest(panel, message);
                return;
            }
        },
        undefined,
        context.subscriptions
    );
}

function getWebviewHtml(port: number): string {
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
