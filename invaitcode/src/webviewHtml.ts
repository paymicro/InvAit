/**
 * Shared HTML template for the Blazor WASM webview.
 *
 * Used by both `extension.ts` (editor tab) and `chatViewProvider.ts` (sidebar)
 * to eliminate duplicated HTML generation logic.
 */

export interface WebviewHtmlOptions {
    /** The port of the local static server serving the Blazor WASM files. */
    staticServerPort: number;
}

/**
 * Returns the HTML string for the webview that hosts the Blazor WASM UI
 * inside an iframe. The iframe loads from the local static server, and
 * a script bridges postMessage between the iframe and the VS Code
 * extension host.
 */
export function getWebviewHtml(options: WebviewHtmlOptions): string {
    const port = options.staticServerPort;

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
