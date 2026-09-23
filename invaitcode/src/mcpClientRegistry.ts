/**
 * McpClientRegistry — TypeScript port of the C# McpClientRegistry.
 *
 * Manages a pool of lazily-started MCP client connections (stdio or HTTP),
 * keyed by serverId. Servers are fingerprinted so that changed launch
 * parameters trigger an automatic restart. Idle servers are reaped after
 * 10 minutes. Transport-death errors trigger a single automatic retry.
 *
 * Output formats (listTools / callTool) are designed to match the C#
 * McpHost JSON wire protocol consumed by the UIBlazor frontend.
 */

import * as fs from 'fs';
import * as path from 'path';
import * as process from 'process';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';

// ---------------------------------------------------------------------------
// Public types / interfaces
// ---------------------------------------------------------------------------

/** Launch info for an MCP server; the registry lazily starts/reuses it by serverId. */
export interface McpServerLaunchInfo {
    serverId: string;
    /** Executable or script command (e.g. "npx"). Resolved against PATH/PATHEXT. */
    command: string;
    /** Argument array passed to the command verbatim. */
    args?: string[];
    /** Working directory for the spawned process. */
    workingDirectory?: string;
    /** HTTP/SSE endpoint URL. When set, url takes precedence over command. */
    url?: string;
    /** Extra HTTP headers for remote servers (e.g. Authorization). */
    headers?: Record<string, string>;
    /** Extra environment variables for stdio servers. */
    env?: Record<string, string>;
}

/** Parameters for listTools — identical to McpServerLaunchInfo. */
export interface McpListToolsParams extends McpServerLaunchInfo {}

/** Parameters for callTool. */
export interface McpCallToolParams extends McpServerLaunchInfo {
    toolName: string;
    /** Tool arguments as a JSON-serialisable object. */
    arguments?: unknown;
    /** Per-call timeout in milliseconds (default 600 000 = 10 min). */
    timeoutMs?: number;
}

/** Parameters for stopServer. */
export interface McpStopServerParams {
    serverId: string;
}

// ---------------------------------------------------------------------------
// Internal types
// ---------------------------------------------------------------------------

/** Tracks a live MCP client connection plus its fingerprint and idle timestamp. */
interface ManagedServer {
    serverId: string;
    client: Client;
    fingerprint: string;
    lastUsedMs: number;
}

/** A simple promise-based mutex that emulates C# SemaphoreSlim(1,1). */
class Semaphore {
    private _queue: (() => void)[] = [];
    private _locked = false;

    async wait(): Promise<void> {
        if (!this._locked) {
            this._locked = true;
            return;
        }
        return new Promise<void>(resolve => this._queue.push(resolve));
    }

    release(): void {
        const next = this._queue.shift();
        if (next) {
            next();
        } else {
            this._locked = false;
        }
    }
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Idle servers are reaped after this duration. */
const IDLE_LIFETIME_MS = 10 * 60 * 1000; // 10 minutes

/** Reaper interval. */
const REAPER_INTERVAL_MS = 60 * 1000; // 1 minute

/** Default call-tool timeout. */
const DEFAULT_CALL_TIMEOUT_MS = 600_000; // 10 minutes

/** listTools timeout. */
const LIST_TOOLS_TIMEOUT_MS = 120_000; // 2 minutes

// ---------------------------------------------------------------------------
// McpClientRegistry
// ---------------------------------------------------------------------------

export class McpClientRegistry {
    /** Active server connections keyed by serverId. */
    private readonly _servers = new Map<string, ManagedServer>();

    /** Serialises server start/restart to avoid double-starting the same id. */
    private readonly _startLock = new Semaphore();

    /** Handle for the idle-reaper interval timer. */
    private _reaperTimer: ReturnType<typeof setInterval> | null = null;

    /** Optional logger; defaults to console.log. */
    private readonly _logger: (msg: string) => void;

    constructor(logger?: (msg: string) => void) {
        this._logger = logger ?? ((msg: string) => console.log('[registry] ' + msg));
        this._reaperTimer = setInterval(() => {
            // Fire-and-forget; errors are caught inside reapIdle.
            void this.reapIdle();
        }, REAPER_INTERVAL_MS);
        // Don't keep the Node.js event loop alive solely for the reaper.
        if (this._reaperTimer && typeof this._reaperTimer.unref === 'function') {
            this._reaperTimer.unref();
        }
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /**
     * Lists the tools exposed by the MCP server identified by `params`.
     * Returns `{ tools: [{ name, description, inputSchema }] }`.
     * Applies a 120-second timeout.
     */
    async listTools(params: McpListToolsParams): Promise<object> {
        return this.withRecovery(params, async (client) => {
            const result = await this.withTimeout(
                () => client.listTools(),
                LIST_TOOLS_TIMEOUT_MS,
                'listTools',
            );

            return {
                tools: result.tools.map((t) => ({
                    name: t.name,
                    description: t.description,
                    inputSchema: t.inputSchema,
                })),
            };
        });
    }

    /**
     * Calls a tool on the MCP server identified by `params`.
     * Returns `{ content: [...], isError: boolean }`.
     *
     * McpProtocolException-equivalent errors (the server returned a protocol
     * error) are caught and returned as a normal error result rather than
     * thrown — matching the C# behaviour.
     */
    async callTool(params: McpCallToolParams): Promise<object> {
        if (!params.toolName || params.toolName.trim() === '') {
            throw new Error('toolName is required.');
        }

        // Build arguments dictionary from params.arguments.
        const args = this.buildArguments(params.arguments);

        return this.withRecovery(params, async (client) => {
            const timeoutMs = Math.max(1000, params.timeoutMs ?? DEFAULT_CALL_TIMEOUT_MS);

            let result;
            try {
                result = await this.withTimeout(
                    () => client.callTool({ name: params.toolName, arguments: args }),
                    timeoutMs,
                    'callTool',
                );
            } catch (ex) {
                // McpProtocolException equivalent: the TS SDK throws an
                // McpError for protocol-level errors. We catch it and
                // return a structured error result, just like the C# version.
                if (this.isMcpProtocolError(ex)) {
                    return {
                        content: [{ type: 'text', text: this.errorMessage(ex) }],
                        isError: true,
                    };
                }
                throw ex;
            }

            return this.serializeToolResult(result);
        });
    }

    /**
     * Stops a single server by id. Returns true if a server was found
     * and stopped, false if it was not running.
     */
    async stopServer(serverId: string): Promise<boolean> {
        const managed = this._servers.get(serverId);
        if (!managed) return false;

        this._servers.delete(serverId);
        await this.disposeClient(managed.serverId, managed.client);
        return true;
    }

    /**
     * Stops all running servers. Returns the count of servers stopped.
     */
    async stopAll(): Promise<number> {
        const ids = Array.from(this._servers.keys());
        let stopped = 0;
        for (const id of ids) {
            if (await this.stopServer(id)) {
                stopped++;
            }
        }
        return stopped;
    }

    /**
     * Returns a snapshot of all running servers with their idle time.
     */
    describeServers(): object[] {
        const now = Date.now();
        return Array.from(this._servers.entries()).map(([id, srv]) => ({
            id,
            idleMs: now - srv.lastUsedMs,
        }));
    }

    /**
     * Stops the reaper timer and all running servers.
     */
    async dispose(): Promise<void> {
        if (this._reaperTimer !== null) {
            clearInterval(this._reaperTimer);
            this._reaperTimer = null;
        }
        await this.stopAll();
    }

    // -----------------------------------------------------------------------
    // Private: core lifecycle
    // -----------------------------------------------------------------------

    /**
     * Returns the client for the given parameters, starting a new one if
     * necessary. If the launch fingerprint has changed since the last start,
     * the old server is stopped and a fresh one is started.
     *
     * Port of C# GetOrStartAsync.
     */
    private async getOrStart(params: McpServerLaunchInfo): Promise<Client> {
        if (!params.serverId || params.serverId.trim() === '') {
            throw new Error('serverId is required.');
        }
        if (
            (!params.command || params.command.trim() === '') &&
            (!params.url || params.url.trim() === '')
        ) {
            throw new Error(
                `command or url is required to start MCP server '${params.serverId}'.`,
            );
        }

        const fingerprint = McpClientRegistry.buildFingerprint(params);

        // Fast path: existing server with matching fingerprint.
        const existing = this._servers.get(params.serverId);
        if (existing && existing.fingerprint === fingerprint) {
            existing.lastUsedMs = Date.now();
            return existing.client;
        }

        // Slow path: acquire lock, double-check, start/restart.
        await this._startLock.wait();
        try {
            const afterLock = this._servers.get(params.serverId);
            if (afterLock) {
                if (afterLock.fingerprint === fingerprint) {
                    afterLock.lastUsedMs = Date.now();
                    return afterLock.client;
                }

                this.log(
                    `MCP server '${params.serverId}' launch parameters changed, restarting it.`,
                );
                await this.stopServer(params.serverId);
            }

            const client = await this.start(params);
            this._servers.set(params.serverId, {
                serverId: params.serverId,
                client,
                fingerprint,
                lastUsedMs: Date.now(),
            });
            this.log(`MCP server '${params.serverId}' started.`);
            return client;
        } finally {
            this._startLock.release();
        }
    }

    /**
     * Creates and connects a new MCP Client using either HTTP or stdio
     * transport based on the parameters.
     *
     * Port of C# StartAsync.
     */
    private async start(params: McpServerLaunchInfo): Promise<Client> {
        let transport: StdioClientTransport | StreamableHTTPClientTransport;

        if (params.url && params.url.trim() !== '') {
            // HTTP/SSE transport.
            const requestInit: { headers?: Record<string, string> } = {};
            if (params.headers && Object.keys(params.headers).length > 0) {
                requestInit.headers = { ...params.headers };
            }
            transport = new StreamableHTTPClientTransport(new URL(params.url), { requestInit });
        } else {
            // stdio transport.
            transport = this.createStdioTransport(params);
        }

        const client = new Client(
            { name: 'invaitcode', version: '0.1.4' },
            { capabilities: {} },
        );

        try {
            await client.connect(transport);
            return client;
        } catch (ex) {
            throw new Error(
                `Failed to start or initialize MCP server '${params.serverId}': ${this.errorMessage(ex)}`,
            );
        }
    }

    /**
     * Builds a StdioClientTransport with resolved command, args, env,
     * and working directory.
     *
     * Port of C# CreateStdioTransport.
     */
    private createStdioTransport(params: McpServerLaunchInfo): StdioClientTransport {
        const resolved = McpClientRegistry.resolveCommand(params.command);
        if (!resolved) {
            throw new Error(`Failed to find '${params.command}' in system.`);
        }

        // Start with the full process environment, then merge custom env.
        const env: Record<string, string> = { ...process.env } as Record<string, string>;
        if (params.env) {
            for (const [key, value] of Object.entries(params.env)) {
                env[key] = value;
            }
        }

        const transportOptions: {
            command: string;
            args?: string[];
            env?: Record<string, string>;
            cwd?: string;
            stderr?: 'pipe' | 'inherit' | 'overlapped';
        } = {
            command: resolved,
            args: params.args ?? [],
            env,
            stderr: 'pipe',
        };

        if (params.workingDirectory && params.workingDirectory.trim() !== '') {
            transportOptions.cwd = params.workingDirectory;
        }

        return new StdioClientTransport(transportOptions);
    }

    // -----------------------------------------------------------------------
    // Private: recovery
    // -----------------------------------------------------------------------

    /**
     * Runs `action` against the lazily-started client. If the transport
     * turns out to be dead (e.g. the server process was killed externally),
     * drops the cached entry, starts a fresh one and retries exactly once;
     * the second failure propagates to the caller.
     *
     * Port of C# WithRecoveryAsync.
     */
    private async withRecovery<T>(
        params: McpServerLaunchInfo,
        action: (client: Client) => Promise<T>,
    ): Promise<T> {
        const client = await this.getOrStart(params);
        try {
            return await action(client);
        } catch (ex) {
            if (this.isTransportDeath(ex)) {
                this.log(
                    `MCP server '${params.serverId}' is unreachable (${this.errorMessage(ex)}), restarting it.`,
                );
                await this.stopServer(params.serverId);
                const fresh = await this.getOrStart(params);
                return await action(fresh);
            }
            throw ex;
        }
    }

    /**
     * Determines whether an error indicates the transport is dead and
     * a retry is worthwhile.
     *
     * NOT transport death: cancellation, argument errors, protocol errors.
     * IS transport death: I/O errors, disposed objects, HTTP errors,
     *   socket errors, or messages containing "exited unexpectedly".
     *
     * Port of C# IsTransportDeath.
     */
    private isTransportDeath(error: unknown): boolean {
        if (error === null || error === undefined) return false;

        // Cancellation / abort — not transport death.
        if (error instanceof Error && error.name === 'AbortError') return false;

        // Argument errors — not transport death.
        if (error instanceof TypeError || error instanceof RangeError) return false;

        // MCP protocol errors — not transport death.
        if (this.isMcpProtocolError(error)) return false;

        const msg = this.errorMessage(error).toLowerCase();
        if (msg.includes('exited unexpectedly')) return true;

        // I/O, disposed, HTTP, socket — transport death.
        if (error instanceof Error) {
            // Node.js system errors often carry a `code` property.
            const code = (error as Error & { code?: string }).code;
            if (
                code === 'ECONNRESET' ||
                code === 'ECONNREFUSED' ||
                code === 'EPIPE' ||
                code === 'EHOSTUNREACH' ||
                code === 'ENOTFOUND' ||
                code === 'ETIMEDOUT' ||
                code === 'ERR_STREAM_DESTROYED' ||
                code === 'ERR_CLOSED' ||
                code === 'ERR_HTTP2_STREAM_ERROR'
            ) {
                return true;
            }

            // Check error name for common transport-death patterns.
            const name = error.name;
            if (
                name === 'IOException' ||
                name === 'ObjectDisposedException' ||
                name === 'HttpRequestException' ||
                name === 'SocketException'
            ) {
                return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Private: fingerprint
    // -----------------------------------------------------------------------

    /**
     * Builds a deterministic fingerprint string from the launch parameters.
     * Two parameter sets with the same fingerprint are considered equivalent.
     *
     * Port of C# BuildFingerprint.
     */
    static buildFingerprint(params: McpServerLaunchInfo): string {
        const env = params.env
            ? Object.keys(params.env)
                .sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()))
                .map((k) => `${k}=${params.env![k]}`)
                .join(';')
            : '';

        const headers =
            params.url && params.headers
                ? Object.keys(params.headers)
                    .sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()))
                    .map((k) => `${k.toLowerCase()}=${params.headers![k]}`)
                    .join(';')
                : '';

        return [
            params.url ?? '',
            params.command ?? '',
            (params.args ?? []).join(' '),
            params.workingDirectory ?? '',
            env,
            headers,
        ].join('|');
    }

    // -----------------------------------------------------------------------
    // Private: command resolution
    // -----------------------------------------------------------------------

    /**
     * Resolves a bare command name (e.g. "npx") to a full executable path
     * by searching PATH with PATHEXT on Windows.
     *
     * On Windows, `npx` needs to be resolved to `npx.cmd` because the TS
     * SDK's StdioClientTransport uses child_process.spawn without shell:true.
     *
     * Port of C# ResolveCommand.
     */
    static resolveCommand(command: string): string | null {
        if (!command || command.trim() === '') return null;

        const isWindows = process.platform === 'win32';

        // If the command is already rooted, check existence directly.
        if (path.isAbsolute(command)) {
            try {
                if (fs.existsSync(command)) return command;
            } catch {
                // ignore
            }
            return null;
        }

        // If the command contains a path separator, check if the file exists
        // relative to the current working directory.
        if (command.includes(path.sep) || command.includes('/')) {
            try {
                if (fs.existsSync(command)) {
                    return path.resolve(command);
                }
            } catch {
                // ignore
            }
            return null;
        }

        // Determine extensions to try.
        // On Windows, if the command doesn't already contain a dot,
        // try each extension in PATHEXT.
        let extensions: string[];
        if (isWindows && !command.includes('.')) {
            const pathext = process.env.PATHEXT ?? '.COM;.EXE;.BAT;.CMD';
            extensions = pathext
                .split(';')
                .map((e) => e.trim())
                .filter((e) => e.length > 0);
        } else {
            extensions = [''];
        }

        // Search PATH.
        const pathVar = process.env.PATH ?? '';
        const pathSeparator = isWindows ? ';' : ':';
        const dirs = pathVar
            .split(pathSeparator)
            .map((d) => d.trim())
            .filter((d) => d.length > 0);

        for (const dir of dirs) {
            for (const ext of extensions) {
                // Normalise extension: ensure it starts with a dot.
                const normalizedExt =
                    ext.length > 0 && !ext.startsWith('.') ? '.' + ext : ext;
                const candidate = path.join(dir, command + normalizedExt);
                try {
                    if (fs.existsSync(candidate)) {
                        return candidate;
                    }
                } catch {
                    // ignore
                }
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Private: serialization
    // -----------------------------------------------------------------------

    /**
     * Serializes a CallToolResult into the wire format expected by the
     * UIBlazor frontend: `{ content: [...], isError: boolean }`.
     *
     * Content blocks are mapped:
     *   text  → { type: "text",  text }
     *   image → { type: "image", data: base64, mimeType }
     *   audio → { type: "audio", data: base64, mimeType }
     *   other → raw JSON of the block
     *
     * Port of C# SerializeToolResult.
     */
    private serializeToolResult(result: { content?: unknown[]; isError?: boolean } | any): object {
        const content: object[] = [];
        const blocks = result.content ?? [];

        for (const block of blocks) {
            const b = block as Record<string, unknown>;
            const type = b['type'] as string | undefined;

            if (type === 'text') {
                content.push({ type: 'text', text: b['text'] ?? '' });
            } else if (type === 'image') {
                content.push({
                    type: 'image',
                    data: this.toBase64(b['data']),
                    mimeType: b['mimeType'] ?? 'image/png',
                });
            } else if (type === 'audio') {
                content.push({
                    type: 'audio',
                    data: this.toBase64(b['data']),
                    mimeType: b['mimeType'] ?? 'audio/mpeg',
                });
            } else {
                // Unknown block type — serialize raw.
                content.push(b);
            }
        }

        return {
            content,
            isError: result.isError ?? false,
        };
    }

    // -----------------------------------------------------------------------
    // Private: idle reaper
    // -----------------------------------------------------------------------

    /**
     * Reaps servers that have been idle longer than IDLE_LIFETIME_MS.
     *
     * Port of C# ReapIdleServersAsync.
     */
    private async reapIdle(): Promise<void> {
        try {
            const now = Date.now();
            // Snapshot keys to avoid mutation-during-iteration issues.
            const entries = Array.from(this._servers.entries());

            for (const [key, managed] of entries) {
                const idleMs = now - managed.lastUsedMs;

                if (idleMs < IDLE_LIFETIME_MS) {
                    // Still fresh — keep it. (It's already in the map.)
                    continue;
                }

                // Idle too long — stop it.
                // Only delete if it's still the same entry (not already replaced).
                const current = this._servers.get(key);
                if (current === managed) {
                    this._servers.delete(key);
                } else {
                    continue; // Already replaced by a newer start.
                }

                this.log(
                    `MCP server '${key}' idle for ${(idleMs / 60000).toFixed(1)} min, stopping it.`,
                );
                await this.disposeClient(managed.serverId, managed.client);
            }
        } catch (ex) {
            this.log('ERROR: idle reaper failed: ' + this.errorMessage(ex));
        }
    }

    // -----------------------------------------------------------------------
    // Private: client disposal
    // -----------------------------------------------------------------------

    /**
     * Closes a client connection, logging success or warning.
     *
     * Port of C# DisposeClientAsync.
     */
    private async disposeClient(serverId: string, client: Client): Promise<void> {
        try {
            await client.close();
            this.log(`MCP server '${serverId}' stopped.`);
        } catch (ex) {
            this.log(`WARN: error while stopping MCP server '${serverId}': ${this.errorMessage(ex)}`);
        }
    }

    // -----------------------------------------------------------------------
    // Private: logging
    // -----------------------------------------------------------------------

    private log(message: string): void {
        this._logger('[registry] ' + message);
    }

    // -----------------------------------------------------------------------
    // Private: helpers
    // -----------------------------------------------------------------------

    /**
     * Wraps a promise with a timeout. Rejects with an AbortError-like
     * error if the timeout fires before the promise settles.
     */
    private withTimeout<T>(
        fn: () => Promise<T>,
        timeoutMs: number,
        operationName: string,
    ): Promise<T> {
        return new Promise<T>((resolve, reject) => {
            const timer = setTimeout(() => {
                reject(
                    new Error(
                        `${operationName} timed out after ${timeoutMs}ms`,
                    ),
                );
            }, timeoutMs);

            fn()
                .then((result) => {
                    clearTimeout(timer);
                    resolve(result);
                })
                .catch((err) => {
                    clearTimeout(timer);
                    reject(err);
                });
        });
    }

    /**
     * Converts the `arguments` field from McpCallToolParams into a
     * plain object suitable for the SDK's callTool method.
     *
     * If arguments is null/undefined, returns an empty object.
     * If arguments is already an object, returns it as-is.
     * Otherwise returns an empty object.
     */
    private buildArguments(args: unknown): Record<string, unknown> {
        if (args === null || args === undefined) return {};
        if (typeof args === 'object' && !Array.isArray(args)) {
            return args as Record<string, unknown>;
        }
        return {};
    }

    /**
     * Checks whether an error is an MCP protocol error (equivalent to
     * C# McpProtocolException). The TS SDK throws errors with the name
     * 'McpError' for protocol-level failures.
     */
    private isMcpProtocolError(error: unknown): boolean {
        if (error instanceof Error) {
            return error.name === 'McpError';
        }
        return false;
    }

    /**
     * Extracts a human-readable message from any thrown value.
     */
    private errorMessage(error: unknown): string {
        if (error instanceof Error) return error.message;
        if (typeof error === 'string') return error;
        return String(error);
    }

    /**
     * Converts binary data (Uint8Array, ArrayBuffer, or base64 string)
     * to a base64 string for serialization.
     */
    private toBase64(data: unknown): string {
        if (typeof data === 'string') {
            // Already a string — could be base64 or raw text.
            // The TS SDK typically provides base64 strings for image/audio.
            return data;
        }
        if (data instanceof Uint8Array) {
            return Buffer.from(data).toString('base64');
        }
        if (data instanceof ArrayBuffer) {
            return Buffer.from(new Uint8Array(data)).toString('base64');
        }
        if (ArrayBuffer.isView(data)) {
            const view = data as ArrayBufferView;
            return Buffer.from(view.buffer as ArrayBuffer, view.byteOffset, view.byteLength).toString('base64');
        }
        // Fallback: JSON-serialize.
        return JSON.stringify(data);
    }
}
