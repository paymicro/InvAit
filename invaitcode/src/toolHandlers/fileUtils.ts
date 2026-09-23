/**
 * File-system helper functions shared across tool handlers.
 */

import * as fs from 'fs';
import * as path from 'path';
import { SKIP_DIRS, MAX_OUTPUT } from './types';

/**
 * Resolves a relative path against the workspace root.
 * Absolute paths are returned as-is.
 */
export function getAbsolutePath(relativePath: string, workspaceRoot: string): string {
    if (!relativePath) throw new Error('Path cannot be null or empty.');
    if (path.isAbsolute(relativePath)) return relativePath;
    const normalized = relativePath.replace(/\//g, path.sep).replace(/^[/\\]+/, '');
    return path.join(workspaceRoot, normalized);
}

/**
 * Converts an absolute path to a workspace-relative path.
 */
export function makeRelative(fullPath: string, workspaceRoot: string): string {
    return path.relative(workspaceRoot, fullPath);
}

/**
 * Recursively walks a directory tree and returns all file paths.
 * Skips directories listed in SKIP_DIRS.
 */
export function walkFiles(dirPath: string, skipDirs: string[] = SKIP_DIRS): string[] {
    const results: string[] = [];
    const entries = fs.readdirSync(dirPath, { withFileTypes: true });
    for (const entry of entries) {
        const fullPath = path.join(dirPath, entry.name);
        if (entry.isDirectory()) {
            if (!skipDirs.includes(entry.name)) {
                results.push(...walkFiles(fullPath, skipDirs));
            }
        } else if (entry.isFile()) {
            results.push(fullPath);
        }
    }
    return results;
}

/**
 * Lists directory contents, optionally recursive.
 */
export function listDir(dirPath: string, recursive: boolean): string[] {
    const entries = fs.readdirSync(dirPath, { withFileTypes: true });
    const results: string[] = [];
    for (const entry of entries) {
        const fullPath = path.join(dirPath, entry.name);
        results.push(fullPath);
        if (recursive && entry.isDirectory()) {
            results.push(...listDir(fullPath, true));
        }
    }
    return results;
}

/**
 * Truncates output longer than MAX_OUTPUT characters, keeping the tail.
 */
export function truncateOutput(output: string): string {
    if (!output || output.length <= MAX_OUTPUT) return output;
    const truncated = output.length - MAX_OUTPUT;
    return `[... ${truncated} characters truncated from the beginning ...]\n${output.substring(output.length - MAX_OUTPUT)}`;
}
