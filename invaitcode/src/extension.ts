import * as vscode from 'vscode';
import * as path from 'path';
import { createStaticServer } from './staticServer';
import { setOnUiReady, disposeMcpRegistry } from './toolDispatcher';
import { registerContextListeners, pushInitialContext, resetContextState, setActivePanel } from './contextPublisher';
import { ChatViewProvider } from './chatViewProvider';
import { initLogger, disposeLogger, log } from './logger';
import { handleWebviewMessage } from './webviewMessageHandler';
import { getWebviewHtml } from './webviewHtml';

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

    panel.webview.html = getWebviewHtml({ staticServerPort: port });

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
            await handleWebviewMessage(message, { panel, context });
        },
        undefined,
        context.subscriptions
    );
}
