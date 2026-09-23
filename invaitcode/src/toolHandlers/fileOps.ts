/**
 * File operation tools: read_files, create_file, edit_files, delete_file, dir.
 */

import * as fs from 'fs';
import * as path from 'path';
import { ToolResult } from './types';
import { getAbsolutePath, makeRelative, listDir } from './fileUtils';
import { findInFile } from './diffMatch';

// 1. read_files
export function readFiles(params: any, workspaceRoot: string): ToolResult {
    const files: any[] = params.files || [];
    const results: any[] = [];

    // Deduplicate by path (keep first)
    const seen = new Set<string>();
    const deduped: any[] = [];
    for (const f of files) {
        const key = (f.path || '').toLowerCase();
        if (!seen.has(key)) {
            seen.add(key);
            deduped.push(f);
        }
    }

    for (const f of deduped) {
        const p: string = f.path;
        const startLine: number = f.startLine ?? -1;
        const lineCount: number = f.lineCount ?? -1;

        try {
            const absPath = getAbsolutePath(p, workspaceRoot);
            if (!fs.existsSync(absPath)) {
                results.push({ path: p, error: "File doesn't exist." });
                continue;
            }

            const content = fs.readFileSync(absPath, 'utf-8');
            let lines = content.split(/\r\n|\r|\n/);
            if (lines.length > 0 && lines[lines.length - 1] === '') {
                lines.pop();
            }

            const skipCount = startLine > 0 ? Math.max(0, startLine - 1) : 0;
            const needsLimit = lineCount <= 0;
            const takeCount = lineCount > 0 ? lineCount : (needsLimit ? 2000 : Infinity);
            const totalLineCount = needsLimit ? lines.length : 0;

            const sliced = lines.slice(skipCount, skipCount + takeCount);

            const entry: any = { path: p, lines: sliced };

            if (skipCount > 0) {
                entry.startLine = skipCount + 1;
            }

            if (needsLimit && totalLineCount > 2000 && skipCount + sliced.length < totalLineCount) {
                entry.totalLines = totalLineCount;
            }

            results.push(entry);
        } catch (e: any) {
            results.push({ path: p, error: e.message ?? String(e) });
        }
    }

    return { success: true, payload: JSON.stringify(results) };
}

// 2. create_file
export function createFile(params: any, workspaceRoot: string): ToolResult {
    try {
        const filePath: string = params.filePath;
        const content: string = params.content ?? '';
        const absPath = getAbsolutePath(filePath, workspaceRoot);
        fs.mkdirSync(path.dirname(absPath), { recursive: true });
        fs.writeFileSync(absPath, content, 'utf-8');
        return { success: true, payload: `File ${filePath} created successfully.` };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 3. edit_files
export function editFiles(params: any, workspaceRoot: string): ToolResult {
    try {
        const filePath: string = params.filePath;
        const edits: any[] = params.edits || [];
        const absPath = getAbsolutePath(filePath, workspaceRoot);

        if (!fs.existsSync(absPath)) {
            return { success: false, error: "File doesn't exist." };
        }

        const rawContent = fs.readFileSync(absPath, 'utf-8');
        const lineEnding = rawContent.includes('\r\n') ? '\r\n' : '\n';
        let lines = rawContent.split(/\r\n|\r|\n/);
        if (lines.length > 0 && lines[lines.length - 1] === '') {
            lines.pop();
        }

        // Sort edits by approximateLine DESCENDING (null treated as -1)
        const sortedEdits = [...edits].sort((a, b) => (b.approximateLine ?? -1) - (a.approximateLine ?? -1));

        let totalReplacements = 0;
        const failures: string[] = [];

        for (const edit of sortedEdits) {
            const search = (edit.oldStr || '').replace(/\r/g, '').split('\n');
            const replace = (edit.newStr || '').replace(/\r/g, '').split('\n');
            const hint = edit.approximateLine ?? -1;

            const actualStart = findInFile(lines, search, hint, 5);

            if (actualStart === -1) {
                failures.push(`Failed to find match for edit at line ${hint}`);
                continue;
            }

            lines.splice(actualStart, search.length, ...replace);
            totalReplacements++;
        }

        if (totalReplacements === 0) {
            return { success: false, error: 'No valid replacements found.' };
        }

        fs.writeFileSync(absPath, lines.join(lineEnding), 'utf-8');

        return {
            success: true,
            payload: `Changes successfully applied to ${filePath}. ${totalReplacements}/${edits.length}.`
        };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 4. delete_file
export function deleteFile(params: any, workspaceRoot: string): ToolResult {
    try {
        const p: string = params.path;
        const absPath = getAbsolutePath(p, workspaceRoot);
        if (fs.existsSync(absPath)) {
            fs.unlinkSync(absPath);
            return { success: true, payload: `File ${p} deleted successfully.` };
        } else {
            return { success: false, error: `File ${p} does not exist.` };
        }
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 5. dir
export function dir(params: any, workspaceRoot: string): ToolResult {
    try {
        const p: string = params.path || '.';
        const recursive: boolean = params.recursive ?? false;
        const absPath = getAbsolutePath(p, workspaceRoot);

        if (!fs.existsSync(absPath) || !fs.statSync(absPath).isDirectory()) {
            return { success: false, error: `Directory ${p} doesn't exist` };
        }

        const items = listDir(absPath, recursive);
        const relativeItems = items.map(full => makeRelative(full, workspaceRoot));
        return { success: true, payload: relativeItems.join('\n') };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}
