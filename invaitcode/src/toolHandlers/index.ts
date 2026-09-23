/**
 * Tool dispatcher — routes tool actions to the appropriate handler.
 *
 * This is the main entry point for the toolHandlers module.
 * All handler implementations are split into focused sub-modules.
 */

import { ToolResult } from './types';
import { readFiles, createFile, editFiles, deleteFile, dir } from './fileOps';
import { searchFiles, grep } from './search';
import { getSkillsMetadata, readSkillContent, getRules, getAgents } from './skills';
import { bash, gitLog, gitDiff } from './bash';
import { getSolutionStructure, getProjectInfo, buildWorkspaceFiles } from './projectInfo';

// Re-export public API for consumers
export { ToolResult, buildWorkspaceFiles };

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
