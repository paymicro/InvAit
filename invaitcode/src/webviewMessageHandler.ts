import * as vscode from 'vscode';
import { handleVsRequest } from './toolDispatcher';
import { handleNetworkProxyRequest, setSkipSslValidation } from './networkProxy';
import { log } from './logger';

/**
 * Context object containing the dependencies needed by the shared
 * webview message handler.
 */
export interface WebviewMessageContext {
    /** The webview panel (or panel-like adapter) that owns the webview. */
    panel: vscode.WebviewPanel;
    /** The extension context — used for globalState storage proxying. */
    context: vscode.ExtensionContext;
}

/**
 * Shared message handler for webview messages coming from the Blazor iframe.
 *
 * Handles:
 *  - NetworkProxyRequest: routes HTTP requests through the extension host proxy
 *  - StorageSet / StorageRemove: proxies localStorage writes to globalState
 *  - skip_ssl_validation: toggles SSL certificate validation
 *  - VsRequest (action + correlationId): delegates to toolDispatcher
 *
 * Used by both `extension.ts` (editor tab) and `chatViewProvider.ts` (sidebar)
 * to eliminate duplicated message-routing logic.
 */
export async function handleWebviewMessage(
    message: unknown,
    ctx: WebviewMessageContext,
): Promise<void> {
    const msg = message as Record<string, unknown>;

    // Network proxy requests (from DynamicEnvironmentHttpMessageHandler)
    if (msg['command'] === 'NetworkProxyRequest') {
        await handleNetworkProxyRequest(ctx.panel, msg['payload'] as Parameters<typeof handleNetworkProxyRequest>[1]);
        return;
    }

    // Storage persistence: proxy localStorage writes to globalState
    if (msg['command'] === 'StorageSet') {
        const payload = msg['payload'] as { key: string; value: unknown };
        await ctx.context.globalState.update(payload.key, payload.value);
        return;
    }
    if (msg['command'] === 'StorageRemove') {
        const payload = msg['payload'] as { key: string };
        await ctx.context.globalState.update(payload.key, undefined);
        return;
    }

    // Skip SSL validation toggle (no correlationId — must be before VsRequest check)
    if (msg['action'] === 'skip_ssl_validation') {
        const skip = msg['payload'] === 'True' || msg['payload'] === 'true';
        setSkipSslValidation(skip);
        return;
    }

    // Tool requests (VsRequest)
    if (msg['action'] && msg['correlationId']) {
        handleVsRequest(ctx.panel, msg as unknown as Parameters<typeof handleVsRequest>[1]);
        return;
    }

    log('[webview] Unhandled message: ' + JSON.stringify(msg).substring(0, 200));
}
