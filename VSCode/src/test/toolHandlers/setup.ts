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

// Imported DIRECTLY and before the `Module._load` hook below, so this is the genuine 'fs' module
// object (a namespace import would hand back a copy whose stubbing is inert).
import fs = require('fs');

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

/**
 * Points BOTH `fs` module identities at one virtual tree and returns a handle on what the code
 * under test did with it.
 *
 * WHY BOTH: the toolHandlers keep a late-bound view of the `fs` object they captured at load time,
 * and which object that is depends on the order mocha loads the test files — this file swaps 'fs'
 * for `fsStub` before importing the toolHandlers barrel, so they are usually bound to `fsStub`, but
 * not guaranteed. Patching both keeps a test independent of that order.
 *
 * `files` is keyed by absolute path (build the keys with `path.join(ROOT, ...)`); a key that is not
 * in the map reads as ENOENT, exactly like the real module.
 */
export interface VirtualFsTree {
    /** Every path handed to `readFileSync`, in order. */
    reads: string[];
    /** Every `writeFileSync` with its exact bytes; the tree is updated, so a later read sees them. */
    writes: { path: string; bytes: Buffer }[];
}

export function stubFsTree(files: Record<string, Buffer | string>): VirtualFsTree {
    const tree: VirtualFsTree = { reads: [], writes: [] };

    const exists = (p: unknown): boolean => Object.prototype.hasOwnProperty.call(files, String(p));

    const read = (p: unknown): Buffer | string => {
        const key = String(p);
        tree.reads.push(key);
        if (!Object.prototype.hasOwnProperty.call(files, key)) {
            const error = new Error(`ENOENT: no such file or directory, open '${key}'`) as Error & { code?: string };
            error.code = 'ENOENT';
            throw error;
        }
        return files[key];
    };

    const write = (p: unknown, data: unknown): void => {
        const bytes = Buffer.from(data as Buffer);
        tree.writes.push({ path: String(p), bytes });
        files[String(p)] = bytes;
    };

    const mkdir = (): undefined => undefined;

    (fsStub.existsSync as sinon.SinonStub).callsFake(exists);
    (fsStub.readFileSync as sinon.SinonStub).callsFake(read);
    (fsStub.writeFileSync as sinon.SinonStub).callsFake(write);
    (fsStub.mkdirSync as sinon.SinonStub).callsFake(mkdir);

    (sinon.stub(fs, 'existsSync') as unknown as sinon.SinonStub).callsFake(exists);
    (sinon.stub(fs, 'readFileSync') as unknown as sinon.SinonStub).callsFake(read);
    (sinon.stub(fs, 'writeFileSync') as unknown as sinon.SinonStub).callsFake(write);
    (sinon.stub(fs, 'mkdirSync') as unknown as sinon.SinonStub).callsFake(mkdir);

    return tree;
}

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
