/**
 * Bash and Git tools: bash, git_log, git_diff.
 *
 * Port of ProcessExecutor.ExecuteBashAsync from ToolCore.
 * On Windows, finds Git Bash (sh.exe) and runs the command via a temp .sh file,
 * mirroring the VS extension approach. On Linux/macOS, uses /bin/sh directly.
 */

import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { execSync, spawnSync } from 'child_process';
import { ToolResult } from './types';
import { truncateOutput } from './fileUtils';

/**
 * Finds the Git Bash sh.exe on Windows.
 * Checks PATH (sh.exe), registry (GitForWindows), and common install paths.
 * Returns null if not found.
 */
function findGitSh(): string | null {
    if (process.platform !== 'win32') {
        // On Linux/macOS, sh is always available
        return '/bin/sh';
    }

    // 1. Check PATH for sh.exe
    const pathDirs = (process.env.PATH || '').split(';').filter(d => d.trim().length > 0);
    const pathext = process.env.PATHEXT || '.COM;.EXE;.BAT;.CMD';
    const exts = pathext.split(';').map(e => e.trim()).filter(e => e.length > 0);
    for (const dir of pathDirs) {
        // Try sh.exe directly
        const candidate = path.join(dir, 'sh.exe');
        if (fs.existsSync(candidate)) return candidate;
        // Try with PATHEXT extensions
        for (const ext of exts) {
            const normalizedExt = ext.startsWith('.') ? ext : '.' + ext;
            const candidateWithExt = path.join(dir, 'sh' + normalizedExt);
            if (fs.existsSync(candidateWithExt) && candidateWithExt.toLowerCase().endsWith('.exe')) {
                return candidateWithExt;
            }
        }
    }

    // 2. Check common Git install paths
    const commonPaths = [
        'C:\\Program Files\\Git\\bin\\sh.exe',
        'C:\\Program Files (x86)\\Git\\bin\\sh.exe',
        path.join(os.homedir(), 'scoop', 'apps', 'git', 'current', 'bin', 'sh.exe'),
    ];
    for (const p of commonPaths) {
        if (fs.existsSync(p)) return p;
    }

    // 3. Try to find via where command
    try {
        const result = execSync('where sh.exe', { encoding: 'utf-8', timeout: 5000 }).trim();
        const lines = result.split(/\r?\n/);
        if (lines.length > 0 && fs.existsSync(lines[0])) {
            return lines[0];
        }
    } catch {
        // sh.exe not found via where
    }

    return null;
}

/**
 * Non-interactive environment variables for bash execution.
 * Port of ProcessExecutor.NonInteractiveEnv from ToolCore.
 */
const NON_INTERACTIVE_ENV: Record<string, string> = {
    TERM: 'dumb',
    NO_COLOR: '1',
    GIT_PAGER: 'cat',
    GIT_CONFIG_PARAMETERS: "'color.ui=false'",
    GIT_TERMINAL_PROMPT: '0',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_CLI_UI_LANGUAGE: 'en-US',
    DOTNET_NOLOGO: 'true',
    DOTNET_TERMINAL_LOGGER: '0',
    LANG: 'en_US.UTF-8',
    LC_ALL: 'en_US.UTF-8',
};

// 12. bash
export function bash(params: any, workspaceRoot: string): ToolResult {
    try {
        const command: string = params.command;
        if (!command) {
            return { success: false, error: 'Command is required.' };
        }

        const shPath = findGitSh();
        if (!shPath) {
            return { success: false, error: 'Git sh not found. Cannot execute bash commands. Install Git for Windows or ensure sh is in PATH.' };
        }

        // Write command to a temp .sh file — avoids quoting/escaping issues and
        // command-line length limits, mirroring ProcessExecutor.ExecuteBashAsync.
        const tempScript = path.join(os.tmpdir(), `invait_${Date.now()}_${Math.random().toString(36).slice(2, 8)}.sh`);
        try {
            fs.writeFileSync(tempScript, command, 'utf-8');

            // Build environment: start with process env, then override non-interactive vars
            const env: Record<string, string> = { ...process.env } as Record<string, string>;
            for (const [key, value] of Object.entries(NON_INTERACTIVE_ENV)) {
                env[key] = value;
            }

            const result = spawnSync(shPath, [tempScript], {
                cwd: workspaceRoot,
                timeout: 120000,
                encoding: 'utf-8',
                maxBuffer: 1024 * 1024 * 10,
                env,
                windowsHide: true,
            });

            // Strip temp file path from output — sh includes it in error messages
            // (e.g. "C:\\...\\invait_xxx.sh: line 1: cd: ..." → "line 1: cd: ...")
            const stripTemp = (s: string) => s.split(tempScript).join('');
            const stdout = stripTemp(result.stdout || '');
            const stderr = stripTemp(result.stderr || '');
            const exitCode = result.status;

            if (result.error) {
                // Process failed to spawn or timed out
                const errMsg = result.error.message || String(result.error);
                if (result.signal === 'SIGTERM' || (result.error as any).code === 'ETIMEDOUT') {
                    return { success: false, error: truncateOutput(`Command timed out after 120000ms.\n${stderr}`) };
                }
                return { success: false, error: truncateOutput(errMsg) };
            }

            if (exitCode === 0) {
                return { success: true, payload: truncateOutput(stdout) };
            }

            // Non-zero exit: combine stdout and stderr for context
            const combined = [stdout, stderr].filter(s => s.trim().length > 0).join('\n');
            return { success: false, error: truncateOutput(combined || `Process exited with code ${exitCode}`) };
        } finally {
            try { fs.unlinkSync(tempScript); } catch { /* ignore */ }
        }
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 14. git_log
export function gitLog(params: any, workspaceRoot: string): ToolResult {
    const num = Math.max(1, Math.min(100, parseInt(String(params.number ?? 10), 10) || 10));
    return bash({
        command: `git log -n ${num} --pretty=format:"%h - %s | %ad" --stat --date=short`
    }, workspaceRoot);
}

// 15. git_diff
export function gitDiff(params: any, workspaceRoot: string): ToolResult {
    const revisions: string = params.revisions || '';
    if (revisions && !/^[\w.,/\-^~@{}]+$/.test(revisions)) {
        return { success: false, error: 'Invalid revision specification.' };
    }
    return bash({
        command: `git diff ${revisions}`
    }, workspaceRoot);
}
