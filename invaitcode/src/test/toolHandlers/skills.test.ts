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
