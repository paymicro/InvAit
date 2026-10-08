/**
 * Solution structure and project info tools:
 * get_solution_structure, get_project_info.
 *
 * Parses .sln / .slnx / .csproj / .fsproj files in the workspace to produce
 * structured project metadata similar to the VS version (GetProjectInfoAsync).
 */

import * as fs from 'fs';
import * as path from 'path';
import { ToolResult, SKIP_EXTENSIONS, SKIP_DIRS, MAX_FILES_PER_DIR } from './types';
import { walkFiles, makeRelative } from './fileUtils';

// ---------------------------------------------------------------------------
// Solution structure walker
// ---------------------------------------------------------------------------

/**
 * Walks the workspace root and returns a list of raw file paths (no formatting).
 * Formatting is done on the UIBlazor side via SolutionTreeBuilder + SolutionTreeFormatter.
 * Skips binary extensions, hidden dirs, and common build/dependency dirs.
 * Max 25 files per directory; excess files are silently skipped.
 */
export function buildWorkspaceFiles(workspaceRoot: string): string[] {
    const result: string[] = [];
    collectFiles(workspaceRoot, result);
    return result;
}

/**
 * Recursively collects file paths from the given directory.
 * Files are collected first, then subdirectories are recursed into.
 */
function collectFiles(dirPath: string, result: string[]): void {
    let entries: fs.Dirent[];
    try {
        entries = fs.readdirSync(dirPath, { withFileTypes: true });
    } catch {
        return;
    }

    const files = entries.filter(e => e.isFile());
    const dirs = entries.filter(e => e.isDirectory());

    // Files first (respecting skip extensions and max count)
    let fileIndex = 0;
    for (const file of files) {
        const ext = path.extname(file.name).toLowerCase();
        if (SKIP_EXTENSIONS.includes(ext)) {
            continue;
        }
        if (fileIndex < MAX_FILES_PER_DIR) {
            result.push(path.join(dirPath, file.name));
            fileIndex++;
        }
        // Excess files are silently skipped — no summary string in raw path list
    }

    // Then directories (skip hidden dirs and skip-list dirs)
    for (const dir of dirs) {
        if (SKIP_DIRS.includes(dir.name) || dir.name[0] === '.') {
            continue;
        }
        collectFiles(path.join(dirPath, dir.name), result);
    }
}

// 11. get_solution_structure
//
// Returns raw file paths (one per line). The ASCII tree formatting is done
// on the UIBlazor side via SolutionTreeBuilder + SolutionTreeFormatter,
// so that the formatting logic is centralized in one place for all IDEs.

export function getSolutionStructure(params: any, workspaceRoot: string): ToolResult {
    try {
        const files = buildWorkspaceFiles(workspaceRoot);
        return { success: true, payload: files.join('\n') };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// ---------------------------------------------------------------------------
// Project info parsing
// ---------------------------------------------------------------------------

interface ProjectEntry {
    name: string;
    relativePath: string;
    absolutePath: string;
}

interface CsprojMetadata {
    outputType: string;
    targetFrameworks: string[];
    sdk: string;
}

/**
 * Finds solution files (.sln or .slnx) in the workspace root directory.
 */
function findSolutionFiles(workspaceRoot: string): string[] {
    const result: string[] = [];
    try {
        const entries = fs.readdirSync(workspaceRoot, { withFileTypes: true });
        for (const entry of entries) {
            if (!entry.isFile()) continue;
            const ext = path.extname(entry.name).toLowerCase();
            if (ext === '.sln' || ext === '.slnx') {
                result.push(path.join(workspaceRoot, entry.name));
            }
        }
    } catch {
        // ignore — directory read errors handled by caller
    }
    return result;
}

/**
 * Parses a classic .sln file to extract project entries.
 * Each "Project(...)" line has the format:
 *   Project("{TypeGuid}") = "{Name}", "{Path}", "{Guid}"
 */
function parseSln(slnPath: string, workspaceRoot: string): ProjectEntry[] {
    const entries: ProjectEntry[] = [];
    const content = fs.readFileSync(slnPath, 'utf-8');
    const projectRegex = /^Project\("\{[0-9A-Fa-f-]+\}"\)\s*=\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"\{[0-9A-Fa-f-]+\}"/gm;
    let match: RegExpExecArray | null;
    while ((match = projectRegex.exec(content)) !== null) {
        const name = match[1];
        const relativePath = match[2].replace(/\\/g, '/');
        const absolutePath = path.resolve(workspaceRoot, relativePath);
        // Skip solution folders (they have .sln extension or no project file extension)
        const ext = path.extname(absolutePath).toLowerCase();
        if (ext === '.csproj' || ext === '.fsproj' || ext === '.vbproj' || ext === '.vcxproj') {
            entries.push({ name, relativePath, absolutePath });
        }
    }
    return entries;
}

/**
 * Parses a .slnx (XML-based solution) file to extract project entries.
 * Each <Project Path="..." /> element references a project.
 */
function parseSlnx(slnxPath: string, workspaceRoot: string): ProjectEntry[] {
    const entries: ProjectEntry[] = [];
    const content = fs.readFileSync(slnxPath, 'utf-8');
    const projectRegex = /<Project\s+[^>]*Path="([^"]+)"[^>]*\/?>/g;
    let match: RegExpExecArray | null;
    while ((match = projectRegex.exec(content)) !== null) {
        const relativePath = match[1].replace(/\\/g, '/');
        const absolutePath = path.resolve(workspaceRoot, relativePath);
        const name = path.basename(absolutePath, path.extname(absolutePath));
        entries.push({ name, relativePath, absolutePath });
    }
    return entries;
}

/**
 * Finds all .csproj/.fsproj/.vbproj files in the workspace by walking the tree.
 * Used as a fallback when no .sln/.slnx is found.
 */
function findProjectFiles(workspaceRoot: string): string[] {
    const allFiles = walkFiles(workspaceRoot);
    const projectExts = ['.csproj', '.fsproj', '.vbproj', '.vcxproj'];
    return allFiles.filter(f => projectExts.includes(path.extname(f).toLowerCase()));
}

/**
 * Parses a .csproj (or .fsproj/.vbproj) XML file to extract metadata:
 * - OutputType (Library, Exe, WinExe, etc.)
 * - TargetFramework(s) (e.g. net10.0, netstandard2.0, v4.8)
 * - Sdk attribute (e.g. Microsoft.NET.Sdk)
 *
 * Handles both SDK-style and old-style project formats.
 */
function parseProjectXml(projectPath: string): CsprojMetadata {
    const metadata: CsprojMetadata = {
        outputType: '',
        targetFrameworks: [],
        sdk: ''
    };

    let content: string;
    try {
        content = fs.readFileSync(projectPath, 'utf-8');
    } catch {
        return metadata;
    }

    // Extract Sdk attribute from <Project Sdk="...">
    const sdkMatch = content.match(/<Project\s+[^>]*Sdk="([^"]+)"/);
    if (sdkMatch) {
        metadata.sdk = sdkMatch[1];
    }

    // Extract OutputType from <OutputType>...</OutputType>
    const outputTypeMatch = content.match(/<OutputType\s*>([^<]+)<\/OutputType\s*>/i);
    if (outputTypeMatch) {
        metadata.outputType = outputTypeMatch[1].trim();
    }

    // Extract TargetFrameworks (plural — semicolon-separated) first
    const tfmsMatch = content.match(/<TargetFrameworks\s*>([^<]+)<\/TargetFrameworks\s*>/i);
    if (tfmsMatch) {
        metadata.targetFrameworks = tfmsMatch[1].split(';').map(t => t.trim()).filter(t => t);
    } else {
        // Try singular TargetFramework
        const tfMatch = content.match(/<TargetFramework\s*>([^<]+)<\/TargetFramework\s*>/i);
        if (tfMatch) {
            metadata.targetFrameworks = [tfMatch[1].trim()];
        } else {
            // Old-style projects use TargetFrameworkVersion (e.g. v4.8)
            const tfvMatch = content.match(/<TargetFrameworkVersion\s*>([^<]+)<\/TargetFrameworkVersion\s*>/i);
            if (tfvMatch) {
                metadata.targetFrameworks = [tfvMatch[1].trim()];
            }
        }
    }

    return metadata;
}

/**
 * Determines the project type string from metadata.
 * Falls back to file extension or 'Unknown'.
 */
function getProjectType(metadata: CsprojMetadata, projectPath: string): string {
    if (metadata.outputType) {
        return metadata.outputType;
    }
    // Infer from SDK attribute
    if (metadata.sdk) {
        const sdk = metadata.sdk.toLowerCase();
        if (sdk.includes('blazor')) return 'Blazor';
        if (sdk.includes('web')) return 'Web';
        if (sdk.includes('worker')) return 'Worker';
    }
    // Infer from extension
    const ext = path.extname(projectPath).toLowerCase();
    switch (ext) {
        case '.csproj': return 'C#';
        case '.fsproj': return 'F#';
        case '.vbproj': return 'VB';
        case '.vcxproj': return 'C++';
        default: return 'Unknown';
    }
}

// 18. get_project_info
//
// Output format per project:
//   Project: {name} | {path} | Type: {type} | TFM: {tfm}
// Falls back to workspace info when no project files are found.

export function getProjectInfo(params: any, workspaceRoot: string): ToolResult {
    try {
        if (!workspaceRoot || !fs.existsSync(workspaceRoot)) {
            return { success: false, error: 'No workspace folder open.' };
        }

        // 1. Try to find solution files in workspace root
        const solutionFiles = findSolutionFiles(workspaceRoot);
        let projectEntries: ProjectEntry[] = [];

        if (solutionFiles.length > 0) {
            // Parse each solution file, collect unique projects
            const seen = new Set<string>();
            for (const slnFile of solutionFiles) {
                const ext = path.extname(slnFile).toLowerCase();
                let entries: ProjectEntry[] = [];
                if (ext === '.slnx') {
                    entries = parseSlnx(slnFile, workspaceRoot);
                } else {
                    entries = parseSln(slnFile, workspaceRoot);
                }
                for (const entry of entries) {
                    const key = entry.absolutePath.toLowerCase();
                    if (!seen.has(key)) {
                        seen.add(key);
                        projectEntries.push(entry);
                    }
                }
            }
        } else {
            // 2. No solution file — find project files directly
            const projectFiles = findProjectFiles(workspaceRoot);
            const seen = new Set<string>();
            for (const pf of projectFiles) {
                const key = pf.toLowerCase();
                if (seen.has(key)) continue;
                seen.add(key);
                projectEntries.push({
                    name: path.basename(pf, path.extname(pf)),
                    relativePath: makeRelative(pf, workspaceRoot),
                    absolutePath: pf
                });
            }
        }

        if (projectEntries.length === 0) {
            // 3. No project files found — fall back to workspace info
            return { success: true, payload: `Workspace: ${workspaceRoot}` };
        }

        // Build output lines with project metadata
        const lines: string[] = [];
        for (const entry of projectEntries) {
            const metadata = parseProjectXml(entry.absolutePath);
            const projectType = getProjectType(metadata, entry.absolutePath);
            const tfm = metadata.targetFrameworks.length > 0
                ? metadata.targetFrameworks.join('; ')
                : 'Unknown';
            lines.push(`Project: ${entry.name} | ${entry.relativePath} | Type: ${projectType} | TFM: ${tfm}`);
        }

        return { success: true, payload: lines.join('\n') };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}
