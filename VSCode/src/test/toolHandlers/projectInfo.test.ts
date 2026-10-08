/**
 * Unit tests for project info and solution structure tools:
 * getProjectInfo, buildWorkspaceFiles, get_solution_structure
 * (toolHandlers/projectInfo.ts).
 */
import * as chai from 'chai';
import chaiAsPromised = require('chai-as-promised');
import * as sinon from 'sinon';
import { fsStub, toolHandlers, dirent, WORKSPACE, resetStubs } from './setup';

chai.use(chaiAsPromised);
const expect = chai.expect;

describe('projectInfo', () => {
    afterEach(() => resetStubs());

    // -----------------------------------------------------------------------
    // buildWorkspaceFiles (returns raw file paths, no formatting)
    // -----------------------------------------------------------------------

    describe('buildWorkspaceFiles', () => {
        it('should list file paths, skipping binary extensions and hidden/node_modules dirs', () => {
            const rootEntries = [
                dirent('src', true),
                dirent('README.md', false),
                dirent('app.exe', false),   // should be skipped (binary ext)
                dirent('image.png', false),  // should be skipped (binary ext)
                dirent('.hidden', true),     // should be skipped (hidden dir)
                dirent('node_modules', true),// should be skipped
            ];
            const srcEntries = [
                dirent('index.ts', false),
                dirent('utils', true),
            ];
            const utilsEntries = [
                dirent('helper.ts', false),
            ];

            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns(rootEntries)
                .onCall(1).returns(srcEntries)
                .onCall(2).returns(utilsEntries);

            (fsStub.statSync as sinon.SinonStub).returns({ size: 1024 });

            const files = toolHandlers.buildWorkspaceFiles(WORKSPACE);

            // Should return an array of file paths
            expect(files).to.be.an('array');
            expect(files).to.have.length(3); // README.md, index.ts, helper.ts

            // Should include README.md, index.ts, helper.ts as full paths
            expect(files.some((f: string) => f.includes('README.md'))).to.be.true;
            expect(files.some((f: string) => f.includes('index.ts'))).to.be.true;
            expect(files.some((f: string) => f.includes('helper.ts'))).to.be.true;

            // Should NOT include exe, png, .hidden, node_modules
            expect(files.some((f: string) => f.includes('app.exe'))).to.be.false;
            expect(files.some((f: string) => f.includes('image.png'))).to.be.false;
            expect(files.some((f: string) => f.includes('.hidden'))).to.be.false;
            expect(files.some((f: string) => f.includes('node_modules'))).to.be.false;
        });

        it('should cap files at 25 per directory and silently skip excess', () => {
            const manyFiles: any[] = [];
            for (let i = 0; i < 30; i++) {
                manyFiles.push(dirent(`file${i}.ts`, false));
            }
            (fsStub.readdirSync as sinon.SinonStub).returns(manyFiles);
            (fsStub.statSync as sinon.SinonStub).returns({ size: 1024 });

            const files = toolHandlers.buildWorkspaceFiles(WORKSPACE);

            // Should return exactly 25 files (no summary string)
            expect(files).to.have.length(25);
            expect(files.some((f: string) => f.includes('file0.ts'))).to.be.true;
            expect(files.some((f: string) => f.includes('file24.ts'))).to.be.true;
            // Should NOT include file25+
            expect(files.some((f: string) => f.includes('file25.ts'))).to.be.false;
            // Should NOT include any summary string
            expect(files.some((f: string) => f.includes('more files'))).to.be.false;
        });

        it('should return an empty array for empty workspace', () => {
            (fsStub.readdirSync as sinon.SinonStub).returns([]);
            const files = toolHandlers.buildWorkspaceFiles(WORKSPACE);
            expect(files).to.be.an('array');
            expect(files).to.have.length(0);
        });

        it('should return raw file paths (not formatted tree)', () => {
            (fsStub.readdirSync as sinon.SinonStub).returns([
                dirent('test.ts', false),
            ]);
            const result = toolHandlers.dispatchTool('get_solution_structure', '{}', WORKSPACE);
            expect(result.success).to.be.true;
            expect(result.payload).to.be.a('string');
            // The payload should be raw file paths, not a formatted tree
            expect(result.payload).to.include('test.ts');
            expect(result.payload).to.not.include('├─');
            expect(result.payload).to.not.include('└─');
        });
    });

    // -----------------------------------------------------------------------
    // get_solution_structure error handling
    // -----------------------------------------------------------------------

    describe('get_solution_structure error handling', () => {
        it('should handle readdirSync errors gracefully (return empty)', () => {
            (fsStub.readdirSync as sinon.SinonStub).throws(new Error('EACCES'));

            const result = toolHandlers.dispatchTool('get_solution_structure', '{}', WORKSPACE);

            // collectFiles catches errors and returns empty, so getSolutionStructure
            // should succeed with empty payload
            expect(result.success).to.be.true;
            expect(result.payload).to.equal('');
        });
    });

    // -----------------------------------------------------------------------
    // getProjectInfo
    // -----------------------------------------------------------------------

    describe('getProjectInfo', () => {
        it('should return error when workspace doesn\'t exist', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(false);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.false;
            expect(result.error).to.include('No workspace folder open');
        });

        it('should parse .sln file and return project info', () => {
            const slnContent =
                'Microsoft Visual Studio Solution File, Format Version 12.00\n' +
                'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MyApp", "src\\MyApp.csproj", "{12345678-1234-1234-1234-123456789012}"\n' +
                'EndProject\n';
            const csprojContent =
                '<Project Sdk="Microsoft.NET.Sdk">\n' +
                '  <PropertyGroup>\n' +
                '    <OutputType>Exe</OutputType>\n' +
                '    <TargetFramework>net10.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            // existsSync: workspaceRoot → true
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            // readdirSync: workspace root returns the .sln file
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('MyApp.sln', false)]);
            // readFileSync: first call → sln content, second call → csproj content
            (fsStub.readFileSync as sinon.SinonStub)
                .onCall(0).returns(slnContent)
                .onCall(1).returns(csprojContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Project: MyApp');
            expect(result.payload).to.include('Type: Exe');
            expect(result.payload).to.include('TFM: net10.0');
        });

        it('should parse .slnx file and return project info', () => {
            const slnxContent =
                '<Solution>\n' +
                '  <Project Path="src/MyApp.csproj" />\n' +
                '</Solution>';
            const csprojContent =
                '<Project Sdk="Microsoft.NET.Sdk">\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFramework>net9.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('MyApp.slnx', false)]);
            (fsStub.readFileSync as sinon.SinonStub)
                .onCall(0).returns(slnxContent)
                .onCall(1).returns(csprojContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Project: MyApp');
            expect(result.payload).to.include('TFM: net9.0');
        });

        it('should find .csproj files when no .sln exists', () => {
            const csprojContent =
                '<Project Sdk="Microsoft.NET.Sdk">\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFramework>net8.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            // readdirSync: workspace root has no .sln, just a src dir
            // Then walkFiles is called: root → [src], src → [App.csproj]
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])  // findSolutionFiles: root
                .onCall(1).returns([dirent('src', true)])  // walkFiles: root
                .onCall(2).returns([dirent('App.csproj', false)]); // walkFiles: src
            (fsStub.readFileSync as sinon.SinonStub).returns(csprojContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Project: App');
            expect(result.payload).to.include('TFM: net8.0');
        });

        it('should return workspace info when no project files found', () => {
            (fsStub.existsSync as sinon.SinonStub).returns(true);
            // readdirSync: workspace root is empty (no .sln, no dirs)
            (fsStub.readdirSync as sinon.SinonStub).returns([]);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Workspace:');
            expect(result.payload).to.include(WORKSPACE);
        });

        it('should parse TargetFrameworks (plural, semicolon-separated)', () => {
            const csprojContent =
                '<Project Sdk="Microsoft.NET.Sdk">\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])  // findSolutionFiles
                .onCall(1).returns([dirent('src', true)])  // walkFiles root
                .onCall(2).returns([dirent('App.csproj', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(csprojContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('TFM: net8.0; net10.0');
        });

        it('should parse TargetFramework (singular)', () => {
            const csprojContent =
                '<Project Sdk="Microsoft.NET.Sdk">\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFramework>net6.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])
                .onCall(1).returns([dirent('src', true)])
                .onCall(2).returns([dirent('App.csproj', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(csprojContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('TFM: net6.0');
        });

        it('should parse OutputType', () => {
            const csprojContent =
                '<Project Sdk="Microsoft.NET.Sdk">\n' +
                '  <PropertyGroup>\n' +
                '    <OutputType>Library</OutputType>\n' +
                '    <TargetFramework>net8.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])
                .onCall(1).returns([dirent('src', true)])
                .onCall(2).returns([dirent('Lib.csproj', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(csprojContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Type: Library');
        });

        it('should infer project type from SDK (Blazor, Web, Worker)', () => {
            const blazorContent =
                '<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFramework>net10.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])
                .onCall(1).returns([dirent('src', true)])
                .onCall(2).returns([dirent('BlazorApp.csproj', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(blazorContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Type: Blazor');
        });

        it('should infer project type from extension (C#, F#, VB, C++)', () => {
            // No OutputType, no SDK → falls back to extension
            const csprojContent =
                '<Project>\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFramework>net8.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])
                .onCall(1).returns([dirent('src', true)])
                .onCall(2).returns([dirent('App.csproj', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(csprojContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Type: C#');
        });

        it('should parse TargetFrameworkVersion (old-style projects)', () => {
            const csprojContent =
                '<Project>\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])
                .onCall(1).returns([dirent('src', true)])
                .onCall(2).returns([dirent('Legacy.csproj', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(csprojContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('TFM: v4.8');
        });

        it('should infer F# project type from .fsproj extension', () => {
            const fsprojContent =
                '<Project Sdk="Microsoft.NET.Sdk">\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFramework>net8.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])
                .onCall(1).returns([dirent('src', true)])
                .onCall(2).returns([dirent('App.fsproj', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(fsprojContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Type: F#');
        });

        it('should infer Web project type from SDK', () => {
            const webContent =
                '<Project Sdk="Microsoft.NET.Sdk.Web">\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFramework>net10.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])
                .onCall(1).returns([dirent('src', true)])
                .onCall(2).returns([dirent('WebApp.csproj', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(webContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Type: Web');
        });

        it('should infer Worker project type from SDK', () => {
            const workerContent =
                '<Project Sdk="Microsoft.NET.Sdk.Worker">\n' +
                '  <PropertyGroup>\n' +
                '    <TargetFramework>net10.0</TargetFramework>\n' +
                '  </PropertyGroup>\n' +
                '</Project>';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('src', true)])
                .onCall(1).returns([dirent('src', true)])
                .onCall(2).returns([dirent('WorkerApp.csproj', false)]);
            (fsStub.readFileSync as sinon.SinonStub).returns(workerContent);

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            expect(result.payload).to.include('Type: Worker');
        });

        it('should handle readFileSync failure in parseProjectXml gracefully', () => {
            const slnContent =
                'Microsoft Visual Studio Solution File, Format Version 12.00\n' +
                'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MyApp", "src\\MyApp.csproj", "{12345678-1234-1234-1234-123456789012}"\n' +
                'EndProject\n';

            (fsStub.existsSync as sinon.SinonStub).returns(true);
            (fsStub.readdirSync as sinon.SinonStub)
                .onCall(0).returns([dirent('MyApp.sln', false)]);
            // First readFileSync: sln content; second: throws (csproj read fails)
            (fsStub.readFileSync as sinon.SinonStub)
                .onCall(0).returns(slnContent)
                .onCall(1).throws(new Error('EACCES'));

            const result = toolHandlers.dispatchTool('get_project_info', '{}', WORKSPACE);

            expect(result.success).to.be.true;
            // Should still return project info with default/empty metadata
            expect(result.payload).to.include('Project: MyApp');
            expect(result.payload).to.include('TFM: Unknown');
        });
    });
});
