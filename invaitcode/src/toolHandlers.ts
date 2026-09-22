import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { execSync } from 'child_process';


// ---------------------------------------------------------------------------
// Public interface
// ---------------------------------------------------------------------------

export interface ToolResult {
    success: boolean;
    payload?: string;
    error?: string;
}

export function dispatchTool(action: string, payloadStr: string | undefined, workspaceRoot: string): ToolResult {
    try {
        const params = JSON.parse(payloadStr || '{}');
        switch (action) {
            case 'read_files':
                return readFiles(params, workspaceRoot);
            case 'create_file':
                return createFile(params, workspaceRoot);
            case 'edit_files':
                return editFiles(params, workspaceRoot);
            case 'delete_file':
                return deleteFile(params, workspaceRoot);
            case 'dir':
                return dir(params, workspaceRoot);
            case 'search_files':
                return searchFiles(params, workspaceRoot);
            case 'grep':
                return grep(params, workspaceRoot);
            case 'get_skills_metadata':
                return getSkillsMetadata(params, workspaceRoot);
            case 'read_skill_content':
                return readSkillContent(params, workspaceRoot);
            case 'get_rules':
                return getRules(params, workspaceRoot);
            case 'get_solution_structure':
                return getSolutionStructure(params, workspaceRoot);
            case 'bash':
                return bash(params, workspaceRoot);
            case 'git_status':
                return bash({ command: 'git status' }, workspaceRoot);
            case 'git_log':
                return gitLog(params, workspaceRoot);
            case 'git_diff':
                return gitDiff(params, workspaceRoot);
            case 'get_agents':
                return getAgents(params, workspaceRoot);
            case 'get_project_info':
                return getProjectInfo(params, workspaceRoot);
            default:
                return { success: false, error: 'Unknown action: ' + action };
        }
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// ---------------------------------------------------------------------------
// Helper functions
// ---------------------------------------------------------------------------

function getAbsolutePath(relativePath: string, workspaceRoot: string): string {
    if (!relativePath) throw new Error('Path cannot be null or empty.');
    if (path.isAbsolute(relativePath)) return relativePath;
    const normalized = relativePath.replace(/\//g, path.sep).replace(/^[/\\]+/, '');
    return path.join(workspaceRoot, normalized);
}

function makeRelative(fullPath: string, workspaceRoot: string): string {
    return path.relative(workspaceRoot, fullPath);
}

function walkFiles(dirPath: string, skipDirs: string[] = ['node_modules', '.git', 'bin', 'obj']): string[] {
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

function listDir(dirPath: string, recursive: boolean): string[] {
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

const MAX_OUTPUT = 30000;

function truncateOutput(output: string): string {
    if (!output || output.length <= MAX_OUTPUT) return output;
    const truncated = output.length - MAX_OUTPUT;
    return `[... ${truncated} characters truncated from the beginning ...]\n${output.substring(output.length - MAX_OUTPUT)}`;
}

// ---------------------------------------------------------------------------
// Diff matching (port from UniversalDiffParser.cs)
// ---------------------------------------------------------------------------

function isMatch(startIdx: number, target: string[], search: string[]): boolean {
    for (let j = 0; j < search.length; j++) {
        const targetLine = target[startIdx + j];
        const searchLine = search[j];
        const trimmedTarget = targetLine.trim();
        const trimmedSearch = searchLine.trim();
        if (trimmedSearch === '') {
            if (trimmedTarget !== '') return false;
            continue;
        }
        if (trimmedTarget.toLowerCase() !== trimmedSearch.toLowerCase()) return false;
    }
    return true;
}

function findInFile(target: string[], search: string[], hint: number, tol: number): number {
    if (!target || !search) return -1;
    if (search.length === 0) return -1;
    if (search.length > target.length) return -1;
    if (tol < 0) tol = 0;

    if (hint !== -1) {
        // Single-line optimization: check exact hint first
        if (search.length === 1) {
            const exactCandidate = hint - 1;
            if (exactCandidate >= 0 && exactCandidate < target.length) {
                if (isMatch(exactCandidate, target, search)) return exactCandidate;
            }
        }
        for (let offset = -tol; offset <= tol; offset++) {
            const candidate = (hint - 1) + offset;
            if (candidate >= 0 && candidate <= target.length - search.length) {
                if (isMatch(candidate, target, search)) return candidate;
            }
        }
        // Fallback to full search
        return findInFile(target, search, -1, tol);
    }

    // Full file search
    for (let i = 0; i <= target.length - search.length; i++) {
        if (isMatch(i, target, search)) return i;
    }
    return -1;
}

// ---------------------------------------------------------------------------
// YAML frontmatter parsing (port from C# skill parsing)
// ---------------------------------------------------------------------------

function parseYamlFrontmatter(lines: string[]): { name: string; description: string; headerLines: number } {
    let name = '';
    let description = '';
    let headerLines = 0;

    if (lines.length === 0) return { name, description, headerLines };

    // Check if first line is "---"
    let idx = 0;
    if (lines[0].trim() !== '---') {
        return { name, description, headerLines };
    }
    idx = 1;

    // Parse until closing "---"
    while (idx < lines.length) {
        const line = lines[idx];
        if (line.trim() === '---') {
            headerLines = idx + 1;
            break;
        }
        const trimmed = line.trim();
        if (trimmed.toLowerCase().startsWith('name:')) {
            name = trimmed.substring(5).trim();
        } else if (trimmed.toLowerCase().startsWith('description:')) {
            description = trimmed.substring(12).trim();
        }
        idx++;
    }

    return { name, description, headerLines };
}

// ---------------------------------------------------------------------------
// Skill file discovery
// ---------------------------------------------------------------------------

function findSkillFiles(workspaceRoot: string): { name: string; filePath: string }[] {
    const skills: { name: string; filePath: string }[] = [];
    const seen = new Set<string>();

    // Local skills (priority)
    const localSkillsDir = path.join(workspaceRoot, 'skills');
    collectSkillFiles(localSkillsDir, skills, seen);

    // Also search recursively in workspace for skills directories
    const localAll = walkFiles(workspaceRoot);
    for (const file of localAll) {
        const basename = path.basename(file);
        if (basename.toUpperCase().endsWith('SKILL.MD') && file.includes(path.sep + 'skills' + path.sep)) {
            const folderName = path.basename(path.dirname(file));
            if (!seen.has(folderName)) {
                seen.add(folderName);
                skills.push({ name: folderName, filePath: file });
            }
        }
    }

    // Global skills
    const globalSkillsDir = path.join(os.homedir(), '.agents', 'skills');
    collectSkillFiles(globalSkillsDir, skills, seen);

    return skills;
}

function collectSkillFiles(dirPath: string, skills: { name: string; filePath: string }[], seen: Set<string>): void {
    if (!fs.existsSync(dirPath) || !fs.statSync(dirPath).isDirectory()) return;
    const allFiles = listDir(dirPath, true);
    for (const file of allFiles) {
        const basename = path.basename(file);
        if (basename.toUpperCase().endsWith('SKILL.MD')) {
            const folderName = path.basename(path.dirname(file));
            if (!seen.has(folderName)) {
                seen.add(folderName);
                skills.push({ name: folderName, filePath: file });
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Solution structure walker
// ---------------------------------------------------------------------------

const SKIP_EXTENSIONS = ['.zip', '.bin', '.dll', '.exe', '.png', '.jpg', '.obj', '.pdb'];
const SKIP_DIRS = ['node_modules', 'bin', 'obj', 'out'];
const MAX_FILES_PER_DIR = 25;

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

// ---------------------------------------------------------------------------
// Tool handlers
// ---------------------------------------------------------------------------

// 1. read_files
function readFiles(params: any, workspaceRoot: string): ToolResult {
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
function createFile(params: any, workspaceRoot: string): ToolResult {
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
function editFiles(params: any, workspaceRoot: string): ToolResult {
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
function deleteFile(params: any, workspaceRoot: string): ToolResult {
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
function dir(params: any, workspaceRoot: string): ToolResult {
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

// 6. search_files
function searchFiles(params: any, workspaceRoot: string): ToolResult {
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
function grep(params: any, workspaceRoot: string): ToolResult {
    try {
        const regexStr: string = params.regex || '';
        const contextLines: number = params.contextLines ?? 3;
        const maxMatches: number = params.maxMatches ?? 50;

        if (!regexStr) {
            return { success: false, error: "Parameter 'query' is required." };
        }

        const regex = new RegExp(regexStr, 'm');
        const skipExtensions = ['.zip', '.bin', '.dll', '.exe', '.png', '.jpg', '.obj', '.pdb'];
        const allFiles = walkFiles(workspaceRoot);

        let totalMatches = 0;
        let output = '';

        for (const file of allFiles) {
            const ext = path.extname(file).toLowerCase();
            if (skipExtensions.includes(ext)) continue;

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

// 8. get_skills_metadata
function getSkillsMetadata(params: any, workspaceRoot: string): ToolResult {
    try {
        const skillFiles = findSkillFiles(workspaceRoot);
        const metadataList: { name: string; description: string }[] = [];

        for (const skill of skillFiles) {
            try {
                const content = fs.readFileSync(skill.filePath, 'utf-8');
                const lines = content.split(/\r\n|\r|\n/);
                const firstLines = lines.slice(0, 10);
                const { name, description } = parseYamlFrontmatter(firstLines);

                if (!name || !description) continue;

                metadataList.push({ name, description });
            } catch {
                continue;
            }
        }

        return { success: true, payload: JSON.stringify(metadataList) };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 9. read_skill_content
function readSkillContent(params: any, workspaceRoot: string): ToolResult {
    try {
        const skillName: string = params.skillName;
        const skillFiles = findSkillFiles(workspaceRoot);

        const skill = skillFiles.find(s => s.name === skillName);
        if (!skill) {
            return { success: false, error: `Skill file not found: ${skillName}` };
        }

        const content = fs.readFileSync(skill.filePath, 'utf-8');
        const lines = content.split(/\r\n|\r|\n/);
        const { name, description, headerLines } = parseYamlFrontmatter(lines);

        const bodyLines = lines.slice(headerLines);
        const bodyContent = bodyLines.join('\n');

        return {
            success: true,
            payload: JSON.stringify({ name, description, content: bodyContent })
        };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 10. get_rules
function getRules(params: any, workspaceRoot: string): ToolResult {
    try {
        const globalRulesPath = path.join(os.homedir(), '.agents', 'rules.md');
        const localRulesPath = path.join(workspaceRoot, '.agents', 'rules.md');

        const globalExists = fs.existsSync(globalRulesPath);
        const localExists = fs.existsSync(localRulesPath);

        if (!globalExists && !localExists) {
            return { success: true, payload: '' };
        }

        let content = '';

        if (globalExists) {
            content += '## Global rules\n' + fs.readFileSync(globalRulesPath, 'utf-8');
        }

        if (localExists) {
            const localContent = fs.readFileSync(localRulesPath, 'utf-8');
            const prefix = globalExists
                ? '\n## Local rules (higher priority) of this project\n'
                : '## Local rules of this project\n';
            content += prefix + localContent;
        }

        return { success: true, payload: content };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 11. get_solution_structure
//
// Returns raw file paths (one per line). The ASCII tree formatting is done
// on the UIBlazor side via SolutionTreeBuilder + SolutionTreeFormatter,
// so that the formatting logic is centralized in one place for all IDEs.

function getSolutionStructure(params: any, workspaceRoot: string): ToolResult {
    try {
        const files = buildWorkspaceFiles(workspaceRoot);
        return { success: true, payload: files.join('\n') };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 12. bash
function bash(params: any, workspaceRoot: string): ToolResult {
    try {
        const command: string = params.command;
        if (!command) {
            return { success: false, error: 'Command is required.' };
        }

        const output = execSync(command, {
            cwd: workspaceRoot,
            timeout: 120000,
            encoding: 'utf-8',
            maxBuffer: 1024 * 1024 * 10
        });

        return { success: true, payload: truncateOutput(output) };
    } catch (e: any) {
        const errOutput = e.stderr || e.message || String(e);
        return { success: false, error: truncateOutput(errOutput) };
    }
}

// 13. git_status — delegates to bash
// (handled in dispatchTool switch)

// 14. git_log
function gitLog(params: any, workspaceRoot: string): ToolResult {
    const num = Math.max(1, Math.min(100, parseInt(String(params.number ?? 10), 10) || 10));
    return bash({
        command: `git log -n ${num} --pretty=format:"%h - %s | %ad" --stat --date=short`
    }, workspaceRoot);
}

// 15. git_diff
function gitDiff(params: any, workspaceRoot: string): ToolResult {
    const revisions: string = params.revisions || '';
    if (revisions && !/^[\w.,/\-^~@{}]+$/.test(revisions)) {
        return { success: false, error: 'Invalid revision specification.' };
    }
    return bash({
        command: `git diff ${revisions}`
    }, workspaceRoot);
}

// 16. get_agents
function getAgents(params: any, workspaceRoot: string): ToolResult {
    try {
        // Case-insensitive search for agents.md (AGENTS.md, Agents.md, etc.)
        const candidates = ['AGENTS.md', 'agents.md', 'Agents.md'];
        const agentsPath = candidates
            .map(c => path.join(workspaceRoot, c))
            .find(p => fs.existsSync(p));
        if (!agentsPath) {
            return { success: false, error: "File agents.md doesn't exist." };
        }
        const content = fs.readFileSync(agentsPath, 'utf-8');
        return { success: true, payload: content };
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 18. get_project_info
//
// Parses .sln / .slnx / .csproj / .fsproj files in the workspace to produce
// structured project metadata similar to the VS version (GetProjectInfoAsync).
// Output format per project:
//   Project: {name} | {path} | Type: {type} | TFM: {tfm}
// Falls back to workspace info when no project files are found.

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

function getProjectInfo(params: any, workspaceRoot: string): ToolResult {
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
