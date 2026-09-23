/**
 * Unit tests for webviewHtml.ts
 *
 * Tests the getWebviewHtml function which returns an HTML string for the
 * Blazor WASM webview. Verifies the DOCTYPE, html lang attribute, iframe
 * src URL with the correct port, acquireVsCodeApi() call, and the
 * postMessage bridge between the iframe and the VS Code extension host.
 *
 * This module has NO external dependencies (no vscode, no fs, etc.),
 * so no module mocking is needed — just import and test directly.
 */
import * as chai from 'chai';

const expect = chai.expect;

import { getWebviewHtml, WebviewHtmlOptions } from '../webviewHtml';

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('webviewHtml', () => {

    describe('getWebviewHtml', () => {
        it('should return a string containing DOCTYPE html', () => {
            const html = getWebviewHtml({ staticServerPort: 5000 });

            expect(html).to.be.a('string');
            expect(html).to.include('<!DOCTYPE html>');
        });

        it('should contain html tag with lang="ru"', () => {
            const html = getWebviewHtml({ staticServerPort: 5000 });

            expect(html).to.include('<html lang="ru">');
        });

        it('should contain iframe with correct port in src URL', () => {
            const port = 5000;
            const html = getWebviewHtml({ staticServerPort: port });

            expect(html).to.include(`src="http://127.0.0.1:${port}/index.html"`);
        });

        it('should contain acquireVsCodeApi() call', () => {
            const html = getWebviewHtml({ staticServerPort: 5000 });

            expect(html).to.include('acquireVsCodeApi()');
        });

        it('should contain message event listener for vscode-webview target', () => {
            const html = getWebviewHtml({ staticServerPort: 5000 });

            expect(html).to.include("addEventListener('message'");
            expect(html).to.include("event.data.target === 'vscode-webview'");
        });

        it('should contain iframe.contentWindow.postMessage for forwarding to Blazor', () => {
            const html = getWebviewHtml({ staticServerPort: 5000 });

            expect(html).to.include('iframe.contentWindow');
            expect(html).to.include('postMessage');
        });

        // -------------------------------------------------------------------
        // Different port numbers
        // -------------------------------------------------------------------

        it('should use port 3000 correctly', () => {
            const html = getWebviewHtml({ staticServerPort: 3000 });

            expect(html).to.include('http://127.0.0.1:3000/index.html');
            expect(html).to.not.include('http://127.0.0.1:5000/index.html');
        });

        it('should use port 8080 correctly', () => {
            const html = getWebviewHtml({ staticServerPort: 8080 });

            expect(html).to.include('http://127.0.0.1:8080/index.html');
            expect(html).to.not.include('http://127.0.0.1:5000/index.html');
        });

        it('should use port 0 correctly', () => {
            const html = getWebviewHtml({ staticServerPort: 0 });

            expect(html).to.include('http://127.0.0.1:0/index.html');
        });

        // -------------------------------------------------------------------
        // Iframe src URL format
        // -------------------------------------------------------------------

        it('should contain the correct iframe src URL format (http://127.0.0.1:PORT/index.html)', () => {
            const port = 5000;
            const html = getWebviewHtml({ staticServerPort: port });

            expect(html).to.include(`http://127.0.0.1:${port}/index.html`);
        });
    });
});
