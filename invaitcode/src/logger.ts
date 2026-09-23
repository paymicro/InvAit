import * as vscode from 'vscode';

let channel: vscode.OutputChannel | null = null;

/**
 * Initialize the Output channel. Call once during activate().
 */
export function initLogger(): void {
    if (!channel) {
        channel = vscode.window.createOutputChannel('InvAit');
    }
}

/**
 * Get the raw channel (for passing as a callback to modules that expect a log function).
 */
export function getChannel(): vscode.OutputChannel | null {
    return channel;
}

/**
 * Log an info message with timestamp.
 */
export function log(message: string): void {
    const ts = new Date().toLocaleTimeString();
    const line = `[${ts}] ${message}`;
    channel?.appendLine(line);
    // Also mirror to developer console for debugging
    console.log(line);
}

/**
 * Log an error message with timestamp.
 */
export function logError(message: string): void {
    const ts = new Date().toLocaleTimeString();
    const line = `[${ts}] [ERROR] ${message}`;
    channel?.appendLine(line);
    console.error(line);
}

/**
 * Log a warning message with timestamp.
 */
export function logWarning(message: string): void {
    const ts = new Date().toLocaleTimeString();
    const line = `[${ts}] [WARN] ${message}`;
    channel?.appendLine(line);
    console.warn(line);
}

/**
 * Show the Output panel in VS Code.
 */
export function showOutput(): void {
    channel?.show(true);
}

/**
 * Dispose the channel. Call during deactivate().
 */
export function disposeLogger(): void {
    channel?.dispose();
    channel = null;
}
