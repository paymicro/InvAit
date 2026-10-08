import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import * as os from 'os';
import { exec, spawn } from 'child_process';
import { dispatchTool, ToolResult } from './toolHandlers';
import { McpClientRegistry } from './mcpClientRegistry';
import { invalidateContextCache } from './contextPublisher';
import { log, logError } from './logger';

const mcpRegistry = new McpClientRegistry((msg: string) => log('[mcp] ' + msg));

export interface VsRequestLike {
    action: string;
    correlationId: string;
    payload?: string;
}

export interface VsResponsePayload {
    correlationId: string;
    success: boolean;
    payload?: string;
    error?: string;
}

/**
 * Dispatches a VsRequest to the appropriate handler.
 * VS Code-specific tools (read_open_file, open_file, build, etc.) are handled here.
 * All other tools are delegated to toolHandlers.ts.
 */
export async function handleVsRequest(
    panel: vscode.WebviewPanel,
    message: VsRequestLike
): Promise<void> {
    const workspaceRoot = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath || '';

    // ui_ready — push initial context
    if (message.action === 'ui_ready') {
        // Context publisher handles this via a callback
        onUiReady?.(workspaceRoot);
        sendResponse(panel, message.correlationId, true, 'OK');
        return;
    }

    // read_open_file — requires VS Code API
    if (message.action === 'read_open_file') {
        const editor = vscode.window.activeTextEditor;
        if (editor && editor.document.fileName) {
            const content = fs.readFileSync(editor.document.fileName, 'utf-8');
            const lines = content.split(/\r\n|\r|\n/);
            if (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();
            const maxLines = 2000;
            const truncated = lines.length > maxLines;
            const fileContent = {
                path: editor.document.fileName,
                lines: lines.slice(0, maxLines),
                totalLines: truncated ? lines.length : undefined
            };
            sendResponse(panel, message.correlationId, true, JSON.stringify([fileContent]));
        } else {
            sendResponse(panel, message.correlationId, false, undefined, 'No active document');
        }
        return;
    }

    // open_file — requires VS Code API
    if (message.action === 'open_file') {
        const filePath = message.payload || '';
        let absPath = path.isAbsolute(filePath) ? filePath : path.join(workspaceRoot, filePath);

        // Fallback to global ~/.agents/ for .agents/ paths (mirrors VS extension logic)
        if (filePath.replace(/\\/g, '/').startsWith('.agents/')) {
            const globalPath = path.join(os.homedir(), filePath.replace(/\\/g, '/'));
            if (!fs.existsSync(absPath) && fs.existsSync(globalPath)) {
                absPath = globalPath;
            }
        }

        const dir = path.dirname(absPath);
        if (dir && !fs.existsSync(dir)) {
            fs.mkdirSync(dir, { recursive: true });
        }
        if (!fs.existsSync(absPath)) {
            fs.writeFileSync(absPath, '');
        }
        vscode.workspace.openTextDocument(absPath).then(doc => {
            vscode.window.showTextDocument(doc);
        });
        sendResponse(panel, message.correlationId, true, `Opened ${filePath}`);
        return;
    }

    // open_folder — requires OS-specific command
    if (message.action === 'open_folder') {
        const folderPath = message.payload || '';
        let absPath = folderPath
            ? (path.isAbsolute(folderPath) ? folderPath : path.join(workspaceRoot, folderPath))
            : workspaceRoot;

        // Fallback to global ~/.agents/ for .agents/ paths (mirrors VS extension logic)
        if (folderPath.replace(/\\/g, '/').startsWith('.agents/')) {
            const globalPath = path.join(os.homedir(), folderPath.replace(/\\/g, '/'));
            if (!fs.existsSync(absPath) && fs.existsSync(globalPath)) {
                absPath = globalPath;
            }
        }

        if (process.platform === 'win32') {
            spawn('explorer.exe', [absPath], { detached: true, stdio: 'ignore' }).unref();
        } else if (process.platform === 'darwin') {
            spawn('open', [absPath], { detached: true, stdio: 'ignore' }).unref();
        } else {
            spawn('xdg-open', [absPath], { detached: true, stdio: 'ignore' }).unref();
        }
        sendResponse(panel, message.correlationId, true, `Folder ${absPath} opened`);
        return;
    }

    // get_error_list — use VS Code diagnostics API
    if (message.action === 'get_error_list') {
        const diagnostics = vscode.languages.getDiagnostics();
        const errors: string[] = [];
        for (const [uri, diags] of diagnostics) {
            for (const d of diags) {
                if (d.severity === vscode.DiagnosticSeverity.Error) {
                    errors.push(` - ${uri.fsPath} | line:${d.range.start.line + 1}\n${d.message}\n`);
                }
            }
        }
        sendResponse(panel, message.correlationId, true, errors.length === 0 ? 'No errors' : errors.join('\n'));
        return;
    }

    // build — use dotnet build
    if (message.action === 'build') {
        exec('dotnet build', { cwd: workspaceRoot, maxBuffer: 1024 * 1024 * 10 }, (error: any, stdout: string, stderr: string) => {
            const output = (stdout + stderr).substring(0, 30000);
            sendResponse(panel, message.correlationId, !error, error ? undefined : 'Build successful.\n' + output, error ? output : undefined);
        });
        return;
    }

    // run_tests — use dotnet test
    if (message.action === 'run_tests') {
        exec('dotnet test --nologo --logger "console;verbosity=normal"', { cwd: workspaceRoot, maxBuffer: 1024 * 1024 * 10, timeout: 300000 }, (error: any, stdout: string, stderr: string) => {
            const output = (stdout + stderr).substring(0, 30000);
            sendResponse(panel, message.correlationId, !error, error ? undefined : output, error ? output : undefined);
        });
        return;
    }

    // open_mcp_settings — open the MCP settings file in the editor
    if (message.action === 'open_mcp_settings') {
        const mcpDir = path.join(os.homedir(), '.agents');
        const mcpFilePath = path.join(mcpDir, 'mcp.json');
        if (!fs.existsSync(mcpDir)) {
            fs.mkdirSync(mcpDir, { recursive: true });
        }
        if (!fs.existsSync(mcpFilePath)) {
            fs.writeFileSync(mcpFilePath, '{\n  "mcp": {}\n}');
        }
        vscode.workspace.openTextDocument(mcpFilePath).then(doc => {
            vscode.window.showTextDocument(doc);
        });
        sendResponse(panel, message.correlationId, true, `Opened ${mcpFilePath}`);
        return;
    }

    // read_mcp_settings_file — read the MCP settings file content
    if (message.action === 'read_mcp_settings_file') {
        // Stop all MCP servers before reading settings
        await mcpRegistry.stopAll();
        const mcpFilePath = path.join(os.homedir(), '.agents', 'mcp.json');
        if (!fs.existsSync(mcpFilePath)) {
            sendResponse(panel, message.correlationId, true, '{"mcpServers":{}}');
            return;
        }
        const content = fs.readFileSync(mcpFilePath, 'utf-8');
        sendResponse(panel, message.correlationId, true, content);
        return;
    }

    // write_mcp_settings — write raw JSON content to the MCP settings file
    if (message.action === 'write_mcp_settings') {
        const content = message.payload || '';
        if (!content || content.trim() === '') {
            sendResponse(panel, message.correlationId, false, undefined, 'Content is empty');
            return;
        }
        // Stop all MCP servers before writing settings
        await mcpRegistry.stopAll();
        const mcpDir = path.join(os.homedir(), '.agents');
        const mcpFilePath = path.join(mcpDir, 'mcp.json');
        if (!fs.existsSync(mcpDir)) {
            fs.mkdirSync(mcpDir, { recursive: true });
        }
        fs.writeFileSync(mcpFilePath, content, 'utf-8');
        sendResponse(panel, message.correlationId, true, 'OK');
        return;
    }

    // mcp_get_tools — list tools from an MCP server
    if (message.action === 'mcp_get_tools') {
        try {
            const args = message.payload ? JSON.parse(message.payload) : {};
            const params = {
                serverId: args.serverId || '',
                command: args.command || '',
                args: args.args || [],
                env: args.env || undefined,
                url: args.url || undefined,
                headers: args.headers || undefined,
                workingDirectory: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath,
            };
            if (!params.serverId) {
                sendResponse(panel, message.correlationId, false, undefined, 'serverId is required');
                return;
            }
            const result = await mcpRegistry.listTools(params);
            sendResponse(panel, message.correlationId, true, JSON.stringify(result));
        } catch (e: any) {
            sendResponse(panel, message.correlationId, false, undefined, e.message);
        }
        return;
    }

    // mcp_call_tool — call a tool on an MCP server
    if (message.action === 'mcp_call_tool') {
        try {
            const args = message.payload ? JSON.parse(message.payload) : {};
            const params = {
                serverId: args.serverId || '',
                command: args.command || '',
                args: args.args || [],
                env: args.env || undefined,
                url: args.url || undefined,
                headers: args.headers || undefined,
                workingDirectory: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath,
                toolName: args.toolName || '',
                arguments: args.arguments,
                timeoutMs: args.timeoutMs || 600000,
            };
            if (!params.serverId || !params.toolName) {
                sendResponse(panel, message.correlationId, false, undefined, 'serverId and toolName are required');
                return;
            }
            const result = await mcpRegistry.callTool(params);
            sendResponse(panel, message.correlationId, true, JSON.stringify(result));
        } catch (e: any) {
            sendResponse(panel, message.correlationId, false, undefined, e.message);
        }
        return;
    }

    // mcp_stop_all — stop all MCP servers
    if (message.action === 'mcp_stop_all') {
        try {
            const stopped = await mcpRegistry.stopAll();
            sendResponse(panel, message.correlationId, true, `Stopped ${stopped} MCP server(s).`);
        } catch (e: any) {
            sendResponse(panel, message.correlationId, false, undefined, e.message);
        }
        return;
    }

    // find_declarations — use VS Code LSP workspace symbol provider
    if (message.action === 'find_declarations') {
        try {
            const args = message.payload ? JSON.parse(message.payload) : {};
            const symbol: string = args.symbol || '';
            if (!symbol) {
                sendResponse(panel, message.correlationId, false, undefined, 'Symbol name is required.');
                return;
            }

            const symbols = await vscode.commands.executeCommand<vscode.SymbolInformation[]>(
                'vscode.executeWorkspaceSymbolProvider', symbol
            );

            if (!symbols || symbols.length === 0) {
                sendResponse(panel, message.correlationId, false, undefined, `Symbol '${symbol}' isn't found.`);
                return;
            }

            const maxResults = 50;
            const limited = symbols.length > maxResults;
            const header = limited
                ? `Found ${symbols.length} declarations (showing first ${maxResults}):\n\n`
                : `Found ${symbols.length} declarations:\n\n`;

            const lines: string[] = [];
            for (let i = 0; i < Math.min(symbols.length, maxResults); i++) {
                const s = symbols[i];
                const filePath = s.location.uri.fsPath;
                const lineNum = s.location.range.start.line + 1;
                const kind = vscode.SymbolKind[s.kind] || String(s.kind);
                const container = s.containerName || '';
                lines.push(`${s.name} | ${kind} | ${container} | ${filePath}:${lineNum}`);
            }

            if (limited) {
                lines.push(`\n+${symbols.length - maxResults} more results. Limited to ${maxResults}.`);
            }

            sendResponse(panel, message.correlationId, true, header + lines.join('\n'));
        } catch (e: any) {
            sendResponse(panel, message.correlationId, false, undefined, e.message);
        }
        return;
    }

    // find_references — use VS Code LSP reference provider
    if (message.action === 'find_references') {
        try {
            const args = message.payload ? JSON.parse(message.payload) : {};
            const symbol: string = args.symbol || '';
            if (!symbol) {
                sendResponse(panel, message.correlationId, false, undefined, 'Symbol name is required.');
                return;
            }

            // Step 1: Find the declaration via workspace symbol provider
            const symbols = await vscode.commands.executeCommand<vscode.SymbolInformation[]>(
                'vscode.executeWorkspaceSymbolProvider', symbol
            );

            if (!symbols || symbols.length === 0) {
                sendResponse(panel, message.correlationId, false, undefined, `Symbol '${symbol}' isn't found.`);
                return;
            }

            // Use the first declaration found to locate references
            const firstSymbol = symbols[0];
            const uri = firstSymbol.location.uri;
            const position = firstSymbol.location.range.start;

            // Step 2: Find all references at the declaration position
            const refs = await vscode.commands.executeCommand<vscode.Location[]>(
                'vscode.executeReferenceProvider', uri, position
            );

            if (!refs || refs.length === 0) {
                sendResponse(panel, message.correlationId, true, `No references found for '${symbol}'.`);
                return;
            }

            const maxResults = 50;
            const limited = refs.length > maxResults;
            const header = limited
                ? `Found ${refs.length} references (showing first ${maxResults}):\n\n`
                : `Found ${refs.length} references:\n\n`;

            const lines: string[] = [];
            for (let i = 0; i < Math.min(refs.length, maxResults); i++) {
                const ref = refs[i];
                const filePath = ref.uri.fsPath;
                const lineNum = ref.range.start.line + 1;

                // Try to read the surrounding code line for context
                let codeLine = '';
                try {
                    const doc = await vscode.workspace.openTextDocument(ref.uri);
                    const lineText = doc.lineAt(Math.min(ref.range.start.line, doc.lineCount - 1)).text;
                    codeLine = lineText.length > 200 ? lineText.substring(0, 200) : lineText;
                } catch {
                    // skip if file can't be read
                }

                lines.push(`${filePath}:${lineNum} | ${codeLine}`);
            }

            if (limited) {
                lines.push(`\n+${refs.length - maxResults} more results. Limited to ${maxResults}.`);
            }

            sendResponse(panel, message.correlationId, true, header + lines.join('\n'));
        } catch (e: any) {
            sendResponse(panel, message.correlationId, false, undefined, e.message);
        }
        return;
    }

    // All other tools — delegate to toolHandlers
    try {
        const result: ToolResult = dispatchTool(message.action, message.payload, workspaceRoot);
        sendResponse(panel, message.correlationId, result.success, result.payload, result.error);
    } catch (e: any) {
        sendResponse(panel, message.correlationId, false, undefined, e.message);
    }

    // Invalidate solution structure cache for filesystem-modifying tools
    // so that subsequent get_solution_structure calls reflect the latest state.
    const fsModifyingActions = ['create_file', 'edit_files', 'delete_file'];
    if (fsModifyingActions.includes(message.action)) {
        invalidateContextCache();
    }
}

// Callback for ui_ready — set by extension.ts
let onUiReady: ((workspaceRoot: string) => void) | null = null;

export function setOnUiReady(callback: (workspaceRoot: string) => void): void {
    onUiReady = callback;
}

function sendResponse(
    panel: vscode.WebviewPanel,
    correlationId: string,
    success: boolean,
    payload?: string,
    error?: string
): void {
    const response: VsResponsePayload = { correlationId, success };
    if (payload !== undefined) response.payload = payload;
    if (error !== undefined) response.error = error;

    panel.webview.postMessage({
        type: 'VsResponse',
        payload: response
    });
}

/**
 * Dispose the MCP registry and stop all MCP servers.
 * Call this on extension deactivation.
 */
export async function disposeMcpRegistry(): Promise<void> {
    try {
        await mcpRegistry.dispose();
    } catch (e: any) {
        logError('[mcp] Error disposing registry: ' + e.message);
    }
}
