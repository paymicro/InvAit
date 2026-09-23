import * as path from 'path';
import * as fs from 'fs';
import * as http from 'http';
import * as vscode from 'vscode';

const MIME_TYPES: { [key: string]: string } = {
    '.html': 'text/html',
    '.css': 'text/css',
    '.js': 'application/javascript',
    '.json': 'application/json',
    '.wasm': 'application/wasm',
    '.dll': 'application/octet-stream',
    '.ico': 'image/x-icon',
    '.png': 'image/png'
};

/**
 * Creates a local HTTP server that serves Blazor WASM static files
 * and a special endpoint for initial localStorage state.
 */
export function createStaticServer(
    wwwrootPath: string,
    context: vscode.ExtensionContext
): Promise<http.Server> {
    return new Promise((resolve, reject) => {
        const server = http.createServer((req, res) => {
            let safeUrl = decodeURIComponent(req.url || '');

            // Strip query parameters (cache busting)
            const qIdx = safeUrl.indexOf('?');
            if (qIdx !== -1) { safeUrl = safeUrl.substring(0, qIdx); }

            // Special endpoint: return all VSCode globalState as JSON for localStorage proxy
            if (safeUrl === '/__vscode_storage__') {
                const storage: Record<string, string> = {};
                for (const key of context.globalState.keys()) {
                    const val = context.globalState.get(key);
                    if (typeof val === 'string') {
                        storage[key] = val;
                    }
                }
                res.writeHead(200, {
                    'Content-Type': 'application/json',
                    'Access-Control-Allow-Origin': '*'
                });
                res.end(JSON.stringify(storage));
                return;
            }

            const resolvedWwwroot = path.resolve(wwwrootPath);
            // Use path.join (not resolve) to construct the path — leading '/' in safeUrl
            // would be treated as absolute by path.resolve on Windows (B:\index.html).
            const filePath = path.resolve(path.join(wwwrootPath, safeUrl === '/' ? 'index.html' : safeUrl));

            // Prevent path traversal: resolved path must be within wwwroot
            if (!filePath.startsWith(resolvedWwwroot + path.sep) && filePath !== resolvedWwwroot) {
                res.writeHead(403, { 'Content-Type': 'text/plain' });
                res.end('403 Forbidden');
                return;
            }

            if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
                const ext = path.extname(filePath).toLowerCase();
                const contentType = MIME_TYPES[ext] || 'application/octet-stream';

                res.writeHead(200, {
                    'Content-Type': contentType,
                    'Access-Control-Allow-Origin': '*'
                });

                // For index.html, inject initial storage data to avoid synchronous XHR in app.js
                if (filePath === path.resolve(path.join(resolvedWwwroot, 'index.html'))) {
                    const storage: Record<string, string> = {};
                    for (const key of context.globalState.keys()) {
                        const val = context.globalState.get(key);
                        if (typeof val === 'string') {
                            storage[key] = val;
                        }
                    }
                    let html = fs.readFileSync(filePath, 'utf8');
                    const injectScript = `<script>window.__vscodeInitialStorage__ = ${JSON.stringify(JSON.stringify(storage))};</script>`;
                    const headIdx = html.indexOf('</head>');
                    if (headIdx !== -1) {
                        html = html.slice(0, headIdx) + injectScript + html.slice(headIdx);
                    } else {
                        html = injectScript + html;
                    }
                    res.end(html);
                } else {
                    fs.createReadStream(filePath).pipe(res);
                }
            } else {
                res.writeHead(404, { 'Content-Type': 'text/plain' });
                res.end('404 Not Found');
            }
        });

        // Raise the listener ceiling so Node.js doesn't warn when many
        // concurrent Blazor WASM requests accumulate internal error listeners.
        server.setMaxListeners(20);

        const onError = (err: Error) => reject(err);

        server.listen(0, '127.0.0.1', () => {
            server.removeListener('error', onError);
            resolve(server);
        });
        server.on('error', onError);
    });
}
