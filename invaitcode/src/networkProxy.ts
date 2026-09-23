import * as vscode from 'vscode';
import * as https from 'https';
import * as http from 'http';
import { log, logError } from './logger';

/**
 * When true, HTTPS requests skip TLS certificate validation.
 * Set by the Blazor UI via the skip_ssl_validation message.
 */
let skipSslValidation = false;

/**
 * User-Agent string injected into every proxied HTTP request.
 * Set once during activate() from vscode.version and extension version.
 * Format: VSCode/{vscodeVersion} (InvAit/{extVersion})
 */
let extensionUserAgent = '';

/**
 * Cached https.Agent with rejectUnauthorized=false.
 * Created lazily on first HTTPS request when skipSslValidation is enabled.
 */
let sslSkipAgent: https.Agent | null = null;

/**
 * Set the User-Agent string for all proxied HTTP requests.
 * Called once during activate().
 */
export function setExtensionUserAgent(value: string): void {
    extensionUserAgent = value;
}

/**
 * Set whether HTTPS requests should skip SSL certificate validation.
 * Called when the Blazor UI sends a skip_ssl_validation message.
 */
export function setSkipSslValidation(value: boolean): void {
    skipSslValidation = value;
    if (value) {
        process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
    } else {
        process.env.NODE_TLS_REJECT_UNAUTHORIZED = '1';
    }
}

/**
 * Handles NetworkProxyRequest messages from Blazor.
 * Fetches the URL in the extension host (bypassing webview sandbox)
 * and streams the response back via postMessage.
 *
 * Uses node:http/https modules instead of global fetch to allow
 * custom TLS agent (rejectUnauthorized=false) for skip-SSL.
 */
export async function handleNetworkProxyRequest(
    panel: vscode.WebviewPanel,
    payload: { requestId: string; url: string; method: string; headers: Record<string, string>; body?: string }
): Promise<void> {
    const { requestId, url, method, headers, body } = payload;
    log('[InvAit Ext] NetworkProxyRequest: ' + method + ' ' + url + ' body length: ' + (body?.length ?? 0));

    try {
        const response = await httpRequest(url, method, headers, body);
        log('[InvAit Ext] Request completed: ' + response.statusCode + ' ' + response.statusMessage);

        const contentType = response.headers['content-type'] || '';
        const isStreaming = contentType.includes('text/event-stream') || contentType.includes('stream');

        // Non-streaming: read full body, send in one chunk
        if (!isStreaming) {
            const responseText = await readFullBody(response);

            panel.webview.postMessage({
                type: 'VsStreamingPacket',
                payload: { type: 'headers', requestId, statusCode: response.statusCode, statusText: response.statusMessage || '' }
            });

            if (responseText) {
                panel.webview.postMessage({
                    type: 'VsStreamingPacket',
                    payload: { type: 'chunk', requestId, chunk: responseText }
                });
            }

            panel.webview.postMessage({
                type: 'VsStreamingPacket',
                payload: { type: 'end', requestId, success: true }
            });
            return;
        }

        // Streaming: forward chunks as they arrive
        panel.webview.postMessage({
            type: 'VsStreamingPacket',
            payload: { type: 'headers', requestId, statusCode: response.statusCode, statusText: response.statusMessage || '' }
        });

        const decoder = new TextDecoder('utf-8');
        let chunkCount = 0;

        for await (const data of response) {
            const chunk = decoder.decode(data, { stream: true });
            chunkCount++;

            panel.webview.postMessage({
                type: 'VsStreamingPacket',
                payload: { type: 'chunk', requestId, chunk }
            });
        }

        log('[InvAit Ext] Streaming done, chunks sent: ' + chunkCount);

        panel.webview.postMessage({
            type: 'VsStreamingPacket',
            payload: { type: 'end', requestId, success: true }
        });

    } catch (error: any) {
        let errorMsg = error.message || 'Request failed';
        if (error.code) {
            errorMsg += ' [code: ' + error.code + ']';
        }
        // Hint for SSL/TLS certificate errors
        if (error.code === 'UNABLE_TO_VERIFY_LEAF_SIGNATURE' ||
            error.code === 'CERT_HAS_EXPIRED' ||
            error.code === 'DEPTH_ZERO_SELF_SIGNED_CERT' ||
            error.code === 'SELF_SIGNED_CERT_IN_CHAIN' ||
            error.code === 'ERR_TLS_CERT_ALTNAME_INVALID') {
            errorMsg += '. Enable "Skip SSL errors" in Settings to bypass certificate validation.';
        }
        logError('[InvAit Ext] Fetch ERROR: ' + errorMsg);
        panel.webview.postMessage({
            type: 'VsStreamingPacket',
            payload: { type: 'end', requestId, success: false, error: errorMsg }
        });
    }
}

/**
 * Makes an HTTP/HTTPS request using node:http/https modules.
 * Returns the IncomingMessage (a Readable stream) for streaming support.
 */
function httpRequest(
    url: string,
    method: string,
    headers: Record<string, string>,
    body?: string
): Promise<http.IncomingMessage> {
    return new Promise((resolve, reject) => {
        const parsedUrl = new URL(url);
        const isHttps = parsedUrl.protocol === 'https:';
        const lib = isHttps ? https : http;

        // Inject User-Agent header (analogous to VS WebView2 Settings.UserAgent)
        const finalHeaders: Record<string, string> = { ...headers };
        if (extensionUserAgent) {
            finalHeaders['User-Agent'] = extensionUserAgent;
        }

        const options: https.RequestOptions = {
            hostname: parsedUrl.hostname,
            port: parsedUrl.port || (isHttps ? 443 : 80),
            path: parsedUrl.pathname + parsedUrl.search,
            method: method,
            headers: finalHeaders,
        };

        // Custom agent for skipping SSL validation
        if (isHttps && skipSslValidation) {
            if (!sslSkipAgent) {
                sslSkipAgent = new https.Agent({ rejectUnauthorized: false });
                log('[InvAit Ext] Created https.Agent with rejectUnauthorized=false for SSL skip');
            }
            options.agent = sslSkipAgent;
        }

        const req = lib.request(options, (response: http.IncomingMessage) => {
            resolve(response);
        });

        req.on('error', (error: any) => {
            reject(error);
        });

        if (body && method !== 'GET' && method !== 'HEAD') {
            req.write(body);
        }

        req.end();
    });
}

/**
 * Reads the full response body as a UTF-8 string.
 */
function readFullBody(response: http.IncomingMessage): Promise<string> {
    return new Promise((resolve, reject) => {
        const chunks: Buffer[] = [];
        response.on('data', (chunk: Buffer) => chunks.push(chunk));
        response.on('end', () => resolve(Buffer.concat(chunks).toString('utf-8')));
        response.on('error', reject);
    });
}
