/**
 * Unit tests for search tools: searchFiles, grep (toolHandlers/search.ts).
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import { fsStub, toolHandlers, dirent, WORKSPACE, resetStubs } from './setup';

chai.use(chaiAsPromised);
const expect = chai.expect;

describe('search', () => {
    afterEach(() => resetStubs());

    // -----------------------------------------------------------------------
    // searchFiles
    // -----------------------------------------------------------------------

    describe('searchFiles', () => {
        it('should find files matching regex pattern', () => {
            // walkFiles: workspace root returns two files
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([
                    dirent('app.ts', false),
                    dirent('index.ts', false),
                ]);
            (fsStub.statSync as sinon.SinonStub).returns({ size: 2048 });

            const payload = JSON.stringify({ regex: '\\.ts$' });
            const result = toolHandlers.dispatchTool('search_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Found 2 files');
            expect(result.payload).to.include('app.ts');
            expect(result.payload).to.include('index.ts');
        });

        it('should return "No files found" when no matches', () => {
            (fsStub.readdirSync as sinon.SinonStub).returns([
                dirent('app.ts', false),
            ]);
            (fsStub.statSync as sinon.SinonStub).returns({ size: 1024 });

            const payload = JSON.stringify({ regex: '\\.java$' });
            const result = toolHandlers.dispatchTool('search_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('No files found');
        });

        it('should return error for empty regex', () => {
            const payload = JSON.stringify({ regex: '' });
            const result = toolHandlers.dispatchTool('search_files', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('not empty');
        });

        it('should limit results to maxMatches', () => {
            // 5 files, maxMatches=2
            const files: any[] = [];
            for (let i = 0; i < 5; i++) {
                files.push(dirent(`file${i}.ts`, false));
            }
            (fsStub.readdirSync as sinon.SinonStub).returns(files);
            (fsStub.statSync as sinon.SinonStub).returns({ size: 512 });

            const payload = JSON.stringify({ regex: '\\.ts$', maxMatches: 2 });
            const result = toolHandlers.dispatchTool('search_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Found 5 files');
            // Should show the +3 extra message
            expect(result.payload).to.include('+3 files');
            expect(result.payload).to.include('Limited to 2');
        });

        it('should include file size in KB in output', () => {
            (fsStub.readdirSync as sinon.SinonStub).returns([
                dirent('big.ts', false),
            ]);
            // 4096 bytes = 4.0 KB
            (fsStub.statSync as sinon.SinonStub).returns({ size: 4096 });

            const payload = JSON.stringify({ regex: '\\.ts$' });
            const result = toolHandlers.dispatchTool('search_files', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('[4.0 KB]');
        });
    });

    // -----------------------------------------------------------------------
    // grep
    // -----------------------------------------------------------------------

    describe('grep', () => {
        it('should find matching lines with context', () => {
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([
                    dirent('code.ts', false),
                ]);
            (fsStub.readFileSync as sinon.SinonStub).returns(
                'line1\nline2\nTODO: fix this\nline4\nline5'
            );

            const payload = JSON.stringify({ regex: 'TODO', contextLines: 1 });
            const result = toolHandlers.dispatchTool('grep', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Found 1 matches');
            expect(result.payload).to.include('code.ts');
            expect(result.payload).to.include('TODO: fix this');
            // Context line before
            expect(result.payload).to.include('line2');
            // Context line after
            expect(result.payload).to.include('line4');
        });

        it('should return "No matches found" when no matches', () => {
            (fsStub.readdirSync as sinon.SinonStub).returns([
                dirent('code.ts', false),
            ]);
            (fsStub.readFileSync as sinon.SinonStub).returns('nothing here\nno match');

            const payload = JSON.stringify({ regex: 'nonexistent_pattern' });
            const result = toolHandlers.dispatchTool('grep', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('No matches found');
        });

        it('should return error for empty regex', () => {
            const payload = JSON.stringify({ regex: '' });
            const result = toolHandlers.dispatchTool('grep', payload, WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include("'query' is required");
        });

        it('should limit to maxMatches', () => {
            (fsStub.readdirSync as sinon.SinonStub).returns([
                dirent('code.ts', false),
            ]);
            (fsStub.readFileSync as sinon.SinonStub).returns(
                'TODO: one\nTODO: two\nTODO: three'
            );

            const payload = JSON.stringify({ regex: 'TODO', maxMatches: 2 });
            const result = toolHandlers.dispatchTool('grep', payload, WORKSPACE);

            expect(result.success).to.be.true;
            // Should show 2+ matches (limited)
            expect(result.payload).to.include('limited to 2');
        });

        it('should skip binary extensions', () => {
            (fsStub.readdirSync as sinon.SinonStub).returns([
                dirent('app.exe', false),
                dirent('code.ts', false),
            ]);
            // readFileSync is called for code.ts only (app.exe is skipped)
            (fsStub.readFileSync as sinon.SinonStub).returns('no match here');

            const payload = JSON.stringify({ regex: 'nonexistent_xyz' });
            const result = toolHandlers.dispatchTool('grep', payload, WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('No matches found');
            // readFileSync should only be called once (for code.ts, not app.exe)
            expect((fsStub.readFileSync as sinon.SinonStub).callCount).to.equal(1);
        });
    });
});
