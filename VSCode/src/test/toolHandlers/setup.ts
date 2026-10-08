/**
 * Shared setup for toolHandlers split tests.
 *
 * Stubs 'fs', 'child_process', and 'os' BEFORE importing toolHandlers so that
 * the module under test receives our mocked versions.  All test files in this
 * directory import the stubs, helpers, and the (already-stubbed) toolHandlers
 * module from here.
 */
import * as sinon from 'sinon';
import * as path from 'path';

// ---------------------------------------------------------------------------
// Stubs
// ---------------------------------------------------------------------------

export const fsStub = {
    readdirSync: sinon.stub(),
    readFileSync: sinon.stub(),
    writeFileSync: sinon.stub(),
    existsSync: sinon.stub(),
    statSync: sinon.stub(),
    unlinkSync: sinon.stub(),
    mkdirSync: sinon.stub(),
    createReadStream: sinon.stub(),
};

export const childProcessStub = {
    execSync: sinon.stub(),
    spawnSync: sinon.stub(),
};

export const osStub = {
    homedir: sinon.stub().returns('/fake/home'),
    tmpdir: sinon.stub().returns('/fake/tmp'),
    platform: sinon.stub().returns('win32'),
};

// ---------------------------------------------------------------------------
// Intercept module loading BEFORE importing toolHandlers.
// toolHandlers does `import * as fs from 'fs'` at module load time, so
// we intercept the require to return our stubbed version.
// ---------------------------------------------------------------------------

const Module = require('module');
const originalLoad = Module._load;
Module._load = function (request: string, parent: any, isMain: boolean) {
    if (request === 'fs') return fsStub;
    if (request === 'child_process') return childProcessStub;
    if (request === 'os') return osStub;
    return originalLoad.call(this, request, parent, isMain);
};

// Now import the module under test — it will receive our stubs.
import * as toolHandlers from '../../toolHandlers';

// Restore after import
Module._load = originalLoad;

export { toolHandlers };

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Create a fake fs.Dirent-like object. */
export function dirent(name: string, isDir: boolean) {
    return {
        name,
        isDirectory: () => isDir,
        isFile: () => !isDir,
        parentPath: '/fake',
    };
}

export const WORKSPACE = path.sep === '\\' ? 'B:\\ws' : '/ws';

/**
 * Reset all stubs to a clean state.  Call in afterEach().
 */
export function resetStubs() {
    sinon.restore();
    // Reset all stubs
    (fsStub.readdirSync as sinon.SinonStub).reset();
    (fsStub.readFileSync as sinon.SinonStub).reset();
    (fsStub.writeFileSync as sinon.SinonStub).reset();
    (fsStub.existsSync as sinon.SinonStub).reset();
    (fsStub.statSync as sinon.SinonStub).reset();
    (fsStub.unlinkSync as sinon.SinonStub).reset();
    (fsStub.mkdirSync as sinon.SinonStub).reset();
    (childProcessStub.execSync as sinon.SinonStub).reset();
    (childProcessStub.spawnSync as sinon.SinonStub).reset();
    // Restore default os stub return values (do NOT reset — they need defaults)
    (osStub.homedir as sinon.SinonStub).returns('/fake/home');
    (osStub.tmpdir as sinon.SinonStub).returns('/fake/tmp');
    (osStub.platform as sinon.SinonStub).returns('win32');
}
