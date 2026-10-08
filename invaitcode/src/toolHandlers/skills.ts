/**
 * Skills, rules, and agents tools:
 * get_skills_metadata, read_skill_content, get_rules, get_agents.
 */

import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { ToolResult } from './types';
import { walkFiles, listDir } from './fileUtils';

// ---------------------------------------------------------------------------
// YAML frontmatter parsing (port from C# skill parsing)
// ---------------------------------------------------------------------------

export function parseYamlFrontmatter(lines: string[]): { name: string; description: string; headerLines: number } {
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
// Tool handlers
// ---------------------------------------------------------------------------

// 8. get_skills_metadata
export function getSkillsMetadata(params: any, workspaceRoot: string): ToolResult {
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
export function readSkillContent(params: any, workspaceRoot: string): ToolResult {
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

// 10. read_skill_reference
export function readSkillReference(params: any, workspaceRoot: string): ToolResult {
    try {
        const skillName: string = params.skillName;
        const fileName: string = params.fileName;

        if (!skillName) {
            return { success: false, error: 'Skill name is required.' };
        }

        if (!fileName) {
            return { success: false, error: 'File name is required.' };
        }

        // Path traversal protection — file name must not contain path separators or ..
        if (fileName.includes('..') || fileName.includes('/') || fileName.includes('\\')) {
            return { success: false, error: 'Invalid file name. Only simple file names are allowed.' };
        }

        const skillFiles = findSkillFiles(workspaceRoot);
        const skill = skillFiles.find(s => s.name === skillName);
        if (!skill) {
            return { success: false, error: `Skill not found: ${skillName}. Make sure skill metadata has been loaded first.` };
        }

        // Skill folder = parent folder of SKILL.md
        const skillFolder = path.dirname(skill.filePath);
        if (!skillFolder) {
            return { success: false, error: `Cannot determine skill folder for: ${skillName}` };
        }

        const referencePath = path.join(skillFolder, 'references', fileName);

        if (!fs.existsSync(referencePath)) {
            // List available references if the folder exists
            const referencesDir = path.join(skillFolder, 'references');
            if (fs.existsSync(referencesDir) && fs.statSync(referencesDir).isDirectory()) {
                const available = fs.readdirSync(referencesDir)
                    .filter(f => f.toLowerCase().endsWith('.md'));
                const list = available.join(', ');
                return {
                    success: false,
                    error: `Reference file '${fileName}' not found in skill '${skillName}'. Available: ${list}`
                };
            }

            return {
                success: false,
                error: `Skill '${skillName}' has no 'references' folder.`
            };
        }

        try {
            const content = fs.readFileSync(referencePath, 'utf-8');
            return { success: true, payload: content };
        } catch (e: any) {
            return { success: false, error: `Error reading reference file: ${e.message ?? String(e)}` };
        }
    } catch (e: any) {
        return { success: false, error: e.message ?? String(e) };
    }
}

// 11. get_rules
export function getRules(params: any, workspaceRoot: string): ToolResult {
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

// 16. get_agents
export function getAgents(params: any, workspaceRoot: string): ToolResult {
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
