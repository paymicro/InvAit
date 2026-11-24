/**
 * Bash and Git tools: bash, git_log, git_diff.
 *
 * Port of ProcessExecutor.ExecuteBashAsync from ToolCore.
 * On Windows, finds Git Bash (sh.exe) and runs the command via a temp .sh file,
 * mirroring the VS extension approach. On Linux/macOS, uses /bin/sh directly.
 *
 * DIVERGENCE from the C# host, kept on purpose: the C# side calls `KillProcessTree`
 * on timeout, while `spawnSync` kills only the direct child. A wrapped build (`sh -c` →
 * `dotnet build`) can therefore outlive the timeout here and keep the CPU busy. The limit
 * and the message are the same, only the reach is smaller — fixing it needs explicit
 * process-group handling, not another timeout knob.
 */

import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { execSync, spawnSync } from 'child_process';
import { ToolResult } from './types';
import { truncateOutput } from './fileUtils';
import { resolveTimeoutMs } from './timeout';
import { sanitizeBashOutput } from './bashOutput';

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

/**
 * Default command timeout, ms. Mirror of `ToolTimeout.DefaultCommandMs`
 * (Shared/Contracts/ToolTimeout.cs): the Node host shares no code with the C# one,
 * so the number has to be kept in sync by hand.
 */
const DEFAULT_TIMEOUT_MS = 120_000;

/**
 * Output cap passed to `spawnSync`. Named because the overflow error message reports the
 * size, and an unnamed literal in two places drifts.
 */
const MAX_BUFFER_BYTES = 1024 * 1024 * 10;

/**
 * Resolves the command limit from the payload. The rule itself lives in `./timeout`
 * (shared with the MCP arm of `toolDispatcher`) so a fix cannot land in one channel only.
 */
function commandTimeoutMs(raw: unknown): number {
    return resolveTimeoutMs(raw, DEFAULT_TIMEOUT_MS);
}

// 12. bash
export function bash(params: any, workspaceRoot: string): ToolResult {
    try {
        const command: string = params.command;
        if (!command) {
            return { success: false, error: 'Command is required.' };
        }

        // Command limit comes from the UI settings (CommonOptions.ToolTimeoutMs) riding in the
        // payload as `timeoutMs` — no longer a hard-coded 120s that disagrees with the setting.
        const timeoutMs = commandTimeoutMs(params.timeoutMs);

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
                timeout: timeoutMs,
                encoding: 'utf-8',
                maxBuffer: MAX_BUFFER_BYTES,
                env,
                windowsHide: true,
            });

            // Strip the temp file path from the output — `sh` prefixes every diagnostic with it
            // ("C:\\...\\invait_xxx.sh: line 1: cd: ..." → "line 1: cd: ..."). The rule lives in
            // ./bashOutput (mirror of ToolCore/BashOutputSanitizer.cs) so a fix cannot land in one
            // channel only; it strips our own file by identity, never "any .sh".
            const stdout = sanitizeBashOutput(result.stdout || '', tempScript);
            const stderr = sanitizeBashOutput(result.stderr || '', tempScript);
            const exitCode = result.status;

            if (result.error) {
                // Two DIFFERENT failures end up here and they must not share a message:
                // a real deadline (ETIMEDOUT) and an output overflow, which Node also reports with
                // `signal: 'SIGTERM'` but `code: 'ENOBUFS'`. Classifying on the signal alone called
                // the overflow "Command timed out", i.e. lied about the reason one layer deeper.
                const errMsg = result.error.message || String(result.error);
                const errorCode = (result.error as NodeJS.ErrnoException).code;
                if (errorCode === 'ETIMEDOUT') {
                    // Keep whatever the command managed to print: the C# host returns stdout+stderr
                    // for a killed command too, and losing the partial log of a long build is the
                    // most expensive part of a timeout. The reported number MUST be the effective
                    // limit — otherwise the message lies about the reason (it used to say 120000
                    // no matter which limit fired).
                    const timeoutNote = `Command timed out after ${timeoutMs}ms.`;
                    const combinedTimeout = [stdout, stderr, timeoutNote]
                        .filter(s => s.trim().length > 0)
                        .join('\n');
                    return { success: false, error: truncateOutput(combinedTimeout) };
                }
                if (errorCode === 'ENOBUFS') {
                    const overflowNote = `Command output exceeded the ${MAX_BUFFER_BYTES / (1024 * 1024)} MB buffer and was killed. Nothing above the limit was captured.`;
                    const combinedOverflow = [stdout, stderr, overflowNote]
                        .filter(s => s.trim().length > 0)
                        .join('\n');
                    return { success: false, error: truncateOutput(combinedOverflow) };
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
    const num = Math.max(1, Math.min(100, Number.parseInt(String(params.number ?? 10), 10) || 10));
    return bash({
        command: `git log -n ${num} --pretty=format:"%h - %s | %ad" --stat --date=short`,
        timeoutMs: params.timeoutMs
    }, workspaceRoot);
}

// 15. git_diff
export function gitDiff(params: any, workspaceRoot: string): ToolResult {
    const revisions: string = params.revisions || '';
    if (revisions && !/^[\w.,/\-^~@{}]+$/.test(revisions)) {
        return { success: false, error: 'Invalid revision specification.' };
    }
    return bash({
        command: `git diff ${revisions}`,
        timeoutMs: params.timeoutMs
    }, workspaceRoot);
}
