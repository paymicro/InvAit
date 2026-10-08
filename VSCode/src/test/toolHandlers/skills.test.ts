/**
 * Unit tests for skills, rules, and agents tools:
 * getSkillsMetadata, readSkillContent, getRules, getAgents
 * (toolHandlers/skills.ts).
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import { fsStub, osStub, toolHandlers, dirent, WORKSPACE, resetStubs } from './setup';

chai.use(chaiAsPromised);
const expect = chai.expect;

describe('skills', () => {
    afterEach(() => resetStubs());

    // -----------------------------------------------------------------------
    // getSkillsMetadata
    // -----------------------------------------------------------------------

    describe('getSkillsMetadata', () => {
        it('should parse YAML frontmatter and return name+description', () => {
            // findSkillFiles: local skills dir doesn't exist, walkFiles returns one SKILL.md
            // collectSkillFiles: existsSync(localSkillsDir) → false, statSync not called
            // walkFiles: workspace root returns [skills/my-skill/SKILL.md]
            (fsStub.existsSync as sinon.SinonStub).returns(false); // local skills dir doesn't exist
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('skills', true)])       // workspace root
                .onCall(1).returns([dirent('my-skill', true)])      // skills dir
                .onCall(2).returns([dirent('SKILL.md', false)]);    // my-skill dir
            (fsStub.readFileSync as sinon.SinonStub).returns(
                '---\nname: My Skill\ndescription: A test skill\n---\n# Content'
            );

            const result = toolHandlers.dispatchTool('get_skills_metadata', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            const parsed = JSON.parse(result.payload!);
            expect(parsed).to.have.length(1);
            expect(parsed[0].name).to.equal('My Skill');
            expect(parsed[0].description).to.equal('A test skill');
        });

        it('should skip skills without name or description', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('skills', true)])
                .onCall(1).returns([dirent('bad-skill', true)])
                .onCall(2).returns([dirent('SKILL.md', false)]);
            // Missing description
            (fsStub.readFileSync as sinon.SinonStub).returns(
                '---\nname: Only Name\n---\n# Content'
            );

            const result = toolHandlers.dispatchTool('get_skills_metadata', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            const parsed = JSON.parse(result.payload!);
            expect(parsed).to.have.length(0);
        });

        it('should return empty array when no skills found', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (fsStub.readdirSync as sinon.SinonStub).returns([]); // empty workspace

            const result = toolHandlers.dispatchTool('get_skills_metadata', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            const parsed = JSON.parse(result.payload!);
            expect(parsed).to.deep.equal([]);
        });
    });

    // -----------------------------------------------------------------------
    // readSkillContent
    // -----------------------------------------------------------------------

    describe('readSkillContent', () => {
        it('should return skill content with frontmatter stripped', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('skills', true)])
                .onCall(1).returns([dirent('my-skill', true)])
                .onCall(2).returns([dirent('SKILL.md', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(
                '---\nname: My Skill\ndescription: A test skill\n---\n# Heading\nBody text here'
            );

            const payload = JSON.stringify({ skillName: 'my-skill' });
            const result = toolHandlers.dispatchTool('read_skill_content', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const parsed = JSON.parse(result.payload!);
            expect(parsed.content).to.include('# Heading');
            expect(parsed.content).to.include('Body text here');
            // Frontmatter should be stripped
            expect(parsed.content).to.not.include('---');
            expect(parsed.content).to.not.include('name:');
        });

        it('should return error when skill not found', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (fsStub.readdirSync as sinon.SinonStub).returns([]);

            const payload = JSON.stringify({ skillName: 'nonexistent' });
            const result = toolHandlers.dispatchTool('read_skill_content', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('Skill file not found');
            expect(result.error).to.include('nonexistent');
        });

        it('should include name and description from frontmatter', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('skills', true)])
                .onCall(1).returns([dirent('my-skill', true)])
                .onCall(2).returns([dirent('SKILL.md', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(
                '---\nname: My Skill\ndescription: A test skill\n---\n# Content'
            );

            const payload = JSON.stringify({ skillName: 'my-skill' });
            const result = toolHandlers.dispatchTool('read_skill_content', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const parsed = JSON.parse(result.payload!);
            expect(parsed.name).to.equal('My Skill');
            expect(parsed.description).to.equal('A test skill');
        });

        it('should include files list when no fileName provided', () => {
            // findSkillFiles call order for existsSync:
            //   call 0: existsSync(localSkillsDir) → false (skip)
            //   call 1: existsSync(globalSkillsDir) → false (skip)
            // Then readSkillContent (no fileName) scans skill folder:
            //   call 2: existsSync(skillFolder) → true (proceed to walkFiles)
            (fsStub.existsSync as sinon.SinonStub)
                .onCall(0).returns(false)   // localSkillsDir in collectSkillFiles
                .onCall(1).returns(false)   // globalSkillsDir in collectSkillFiles
                .onCall(2).returns(true);   // skillFolder exists check (for file listing scan)
            (fsStub.statSync as sinon.SinonStub).returns({ isDirectory: () => true });
            // readdirSync calls:
            // 0: workspace root (walkFiles in findSkillFiles)
            // 1: skills dir (walkFiles)
            // 2: my-skill dir (walkFiles) — has SKILL.md + references/
            // 3: references dir (walkFiles) — has api.md
            // 4: skillFolder (walkFiles for file listing)
            // 5: skillFolder/references (walkFiles for file listing)
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('skills', true)])
                .onCall(1).returns([dirent('my-skill', true)])
                .onCall(2).returns([dirent('SKILL.md', false), dirent('references', true)])
                .onCall(3).returns([dirent('api.md', false)])
                .onCall(4).returns([dirent('SKILL.md', false), dirent('references', true)])
                .onCall(5).returns([dirent('api.md', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(
                '---\nname: My Skill\ndescription: A test skill\n---\n# Content'
            );

            const payload = JSON.stringify({ skillName: 'my-skill' });
            const result = toolHandlers.dispatchTool('read_skill_content', payload, WORKSPACE);

            expect(result.success).to.be.true;
            const parsed = JSON.parse(result.payload!);
            expect(parsed.files).to.be.an('array');
            expect(parsed.files).to.include('references/api.md');
            // SKILL.md should NOT be in the files list
            expect(parsed.files).to.not.include('SKILL.md');
        });
    });

    // -----------------------------------------------------------------------
    // readSkillContent with fileName (merged read_skill_reference)
    // -----------------------------------------------------------------------

    describe('readSkillContent with fileName', () => {
        it('should read a specific file from the skill folder', () => {
            // findSkillFiles call order for existsSync:
            //   call 0: existsSync(localSkillsDir) → false (skip)
            //   call 1: existsSync(globalSkillsDir) → false (skip)
            // Then readSkillContent (with fileName) checks resolved file:
            //   call 2: existsSync(resolved) → true (file exists)
            (fsStub.existsSync as sinon.SinonStub)
                .onCall(0).returns(false)   // localSkillsDir in collectSkillFiles
                .onCall(1).returns(false)   // globalSkillsDir in collectSkillFiles
                .onCall(2).returns(true);   // resolved file exists
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('skills', true)])       // workspace root (walkFiles)
                .onCall(1).returns([dirent('my-skill', true)])      // skills dir (walkFiles)
                .onCall(2).returns([dirent('SKILL.md', false)]);    // my-skill dir (walkFiles)
            // readFileSync returns the reference file content
            (fsStub.readFileSync as sinon.SinonStub).returns('Reference file content here');

            const payload = JSON.stringify({ skillName: 'my-skill', fileName: 'references/api.md' });
            const result = toolHandlers.dispatchTool('read_skill_content', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.equal('Reference file content here');
        });

        it('should read a file from a subdirectory (references/api-spec.md)', () => {
            // findSkillFiles: existsSync(localSkillsDir) → false, existsSync(globalSkillsDir) → false
            // Then existsSync(resolved) → true
            (fsStub.existsSync as sinon.SinonStub)
                .onCall(0).returns(false)   // localSkillsDir
                .onCall(1).returns(false)   // globalSkillsDir
                .onCall(2).returns(true);   // resolved file exists
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('skills', true)])
                .onCall(1).returns([dirent('my-skill', true)])
                .onCall(2).returns([dirent('SKILL.md', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns('API spec content');

            const payload = JSON.stringify({ skillName: 'my-skill', fileName: 'references/api-spec.md' });
            const result = toolHandlers.dispatchTool('read_skill_content', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.equal('API spec content');
        });

        it('should block path traversal attempts (../../etc/passwd)', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('skills', true)])
                .onCall(1).returns([dirent('my-skill', true)])
                .onCall(2).returns([dirent('SKILL.md', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns('should not reach here');

            const payload = JSON.stringify({ skillName: 'my-skill', fileName: '../../etc/passwd' });
            const result = toolHandlers.dispatchTool('read_skill_content', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('escapes the skill folder');
        });

        it('should return error when file not found', () => {
            // existsSync: false for localSkillsDir, false for globalSkillsDir, false for resolved file
            (fsStub.existsSync as sinon.SinonStub).returns(false);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('skills', true)])
                .onCall(1).returns([dirent('my-skill', true)])
                .onCall(2).returns([dirent('SKILL.md', false)]);

            const payload = JSON.stringify({ skillName: 'my-skill', fileName: 'nonexistent.md' });
            const result = toolHandlers.dispatchTool('read_skill_content', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('not found');
        });
    });

    // -----------------------------------------------------------------------
    // getRules
    // -----------------------------------------------------------------------

    describe('getRules', () => {
        beforeEach(() => {
            (osStub.homedir as sinon.SinonStub).returns('/fake/home');
        });

        it('should return empty string when no rules files exist', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const result = toolHandlers.dispatchTool('get_rules', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.equal('');
        });

        it('should return global rules only when local doesn\'t exist', () => {
            // First call: globalRulesPath exists → true; second: localRulesPath → false
            (fsStub.existsSync as sinon.SinonStub)
                .onCall(0).returns(true)
                .onCall(1).returns(false);
            (fsStub.readFileSync as sinon.SinonStub).returns('Global rule content');

            const result = toolHandlers.dispatchTool('get_rules', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('## Global rules');
            expect(result.payload).to.include('Global rule content');
            // Should NOT include local rules header
            expect(result.payload).to.not.include('Local rules');
        });

        it('should return local rules only when global doesn\'t exist', () => {
            (fsStub.existsSync as sinon.SinonStub)
                .onCall(0).returns(false)
                .onCall(1).returns(true);
            (fsStub.readFileSync as sinon.SinonStub).returns('Local rule content');

            const result = toolHandlers.dispatchTool('get_rules', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('## Local rules of this project');
            expect(result.payload).to.include('Local rule content');
            // Should NOT include global rules header
            expect(result.payload).to.not.include('## Global rules');
        });

        it('should combine global and local rules with proper headers', () => {
            (fsStub.existsSync as sinon.SinonStub)
                .onCall(0).returns(true)
                .onCall(1).returns(true);
            (fsStub.readFileSync as sinon.SinonStub)
                .onCall(0).returns('Global content')
                .onCall(1).returns('Local content');

            const result = toolHandlers.dispatchTool('get_rules', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('## Global rules');
            expect(result.payload).to.include('Global content');
            expect(result.payload).to.include('## Local rules (higher priority) of this project');
            expect(result.payload).to.include('Local content');
            // Global should come before local
            expect(result.payload!.indexOf('Global content')).to.be.lessThan(result.payload!.indexOf('Local content'));
        });
    });

    // -----------------------------------------------------------------------
    // getAgents
    // -----------------------------------------------------------------------

    describe('getAgents', () => {
        it('should read AGENTS.md when it exists', () => {
            (fsStub.existsSync as sinon.SinonStub)
                .onCall(0).returns(true); // AGENTS.md exists
            (fsStub.readFileSync as sinon.SinonStub).returns('# Agents\nRules here');

            const result = toolHandlers.dispatchTool('get_agents', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Rules here');
        });

        it('should return error when no agents.md file found', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const result = toolHandlers.dispatchTool('get_agents', '{}', WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include("agents.md doesn't exist");
        });
    });
});
