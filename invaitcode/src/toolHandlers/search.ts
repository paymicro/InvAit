/**
 * Search tools: search_files, grep.
 */

import * as fs from 'fs';
import * as path from 'path';
import { ToolResult, SKIP_EXTENSIONS } from './types';
import { walkFiles, makeRelative, truncateOutput } from './fileUtils';

// 6. search_files
export function searchFiles(params: any, workspaceRoot: string): ToolResult {
    try {
        const regexStr: string = params.regex || '';
        const maxMatches: number = params.maxMatches ?? 50;

        if (!regexStr) {
            return { success: false, error: 'Regex pattern should be not empty.' };
        }

        const regex = new RegExp(regexStr);
        const allFiles = walkFiles(workspaceRoot);

        const matches: { path: string; sizeKB: number }[] = [];
        let totalFound = 0;

        for (const file of allFiles) {
            const relativePath = makeRelative(file, workspaceRoot);
            if (regex.test(relativePath)) {
                totalFound++;
                if (matches.length < maxMatches) {
                    const stat = fs.statSync(file);
                    matches.push({ path: relativePath, sizeKB: stat.size / 1024 });
                }
            }
        }

        if (matches.length === 0) {
            return { success: true, payload: 'No files found matching pattern.' };
        }

        let output = `Found ${totalFound} files matching pattern:\n\n`;
        for (const m of matches) {
            output += `${m.path} [${m.sizeKB.toFixed(1)} KB]\n`;
        }

        if (totalFound > maxMatches) {
            output += `\n+${totalFound - maxMatches} files. Limited to ${maxMatches}`;
        }

        return { success: true, payload: output };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 7. grep
export function grep(params: any, workspaceRoot: string): ToolResult {
    try {
        const regexStr: string = params.regex || '';
        const contextLines: number = params.contextLines ?? 3;
        const maxMatches: number = params.maxMatches ?? 50;

        if (!regexStr) {
            return { success: false, error: "Parameter 'query' is required." };
        }

        const regex = new RegExp(regexStr, 'm');
        const allFiles = walkFiles(workspaceRoot);

        let totalMatches = 0;
        let output = '';

        for (const file of allFiles) {
            const ext = path.extname(file).toLowerCase();
            if (SKIP_EXTENSIONS.includes(ext)) continue;

            const relativePath = makeRelative(file, workspaceRoot);
            let content: string;
            try {
                content = fs.readFileSync(file, 'utf-8');
            } catch {
                continue;
            }

            const lines = content.split(/\r\n|\r|\n/);
            const matchIndices: number[] = [];

            for (let i = 0; i < lines.length; i++) {
                if (regex.test(lines[i])) {
                    matchIndices.push(i);
                    totalMatches++;
                    if (totalMatches >= maxMatches) break;
                }
            }

            if (matchIndices.length === 0) continue;

            output += `### ${relativePath}\nMatches: ${matchIndices.length}\n\n`;

            for (const matchIdx of matchIndices) {
                const start = Math.max(0, matchIdx - contextLines);
                const end = Math.min(lines.length - 1, matchIdx + contextLines);

                for (let i = start; i <= end; i++) {
                    const marker = i === matchIdx ? '>' : ' ';
                    const lineNum = String(i + 1).padStart(4, ' ');
                    let lineContent = lines[i];
                    if (lineContent.length > 200) {
                        lineContent = lineContent.substring(0, 200);
                    }
                    output += `${marker} ${lineNum} | ${lineContent}\n`;
                }
                output += '---\n';
            }

            if (totalMatches >= maxMatches) break;
        }

        if (!output) {
            return { success: true, payload: 'No matches found.' };
        }

        const header = totalMatches >= maxMatches
            ? `Found ${totalMatches}+ matches (limited to ${maxMatches}):\n\n`
            : `Found ${totalMatches} matches:\n\n`;
        output = truncateOutput(header + output);

        return { success: true, payload: output };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}
