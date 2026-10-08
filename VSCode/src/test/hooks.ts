/**
 * ts-node require hooks for mocha.
 *
 * Intercepts `require('vscode')` and returns our mock so that source
 * modules importing 'vscode' can be loaded outside the VS Code runtime.
 */
const Module = require('module');
const path = require('path');

// Resolve our mock module path
const vscodeMockPath = path.resolve(__dirname, 'vscodeMock.js');

// Store the original resolveFilename
const originalResolveFilename = Module._resolveFilename;

Module._resolveFilename = function (request: string, parent: any, ...rest: any[]) {
    if (request === 'vscode') {
        // Return the mock path — ts-node will compile vscodeMock.ts on the fly
        return path.resolve(__dirname, 'vscodeMock.ts');
    }
    return originalResolveFilename.call(this, request, parent, ...rest);
};

// Also intercept Module._load for safety (some import styles bypass _resolveFilename)
const originalLoad = Module._load;
Module._load = function (request: string, parent: any, isMain: boolean) {
    if (request === 'vscode') {
        // Load our mock through ts-node
        const mockPath = path.resolve(__dirname, 'vscodeMock.ts');
        return originalLoad.call(this, mockPath, parent, isMain);
    }
    return originalLoad.call(this, request, parent, isMain);
};
