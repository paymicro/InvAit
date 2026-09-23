/**
 * Shared types and constants for tool handlers.
 */

export interface ToolResult {
    success: boolean;
    payload?: string;
    error?: string;
}

export const SKIP_EXTENSIONS = ['.zip', '.bin', '.dll', '.exe', '.png', '.jpg', '.obj', '.pdb', '.wasm', '.br', '.gz'];
export const SKIP_DIRS = ['node_modules', 'bin', 'obj', 'out', 'TestResults'];
export const MAX_FILES_PER_DIR = 25;
export const MAX_OUTPUT = 30000;
