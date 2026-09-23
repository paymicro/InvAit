/**
 * esbuild bundling configuration for VS Code extension.
 *
 * Bundles all TypeScript sources + production dependencies into a single
 * file so the .vsix does not need to ship node_modules.
 */
const esbuild = require('esbuild');

/** @type {import('esbuild').BuildOptions} */
const buildOptions = {
    entryPoints: ['src/extension.ts'],
    bundle: true,
    outfile: 'dist/extension.js',
    external: ['vscode'],              // vscode is provided by the host
    format: 'cjs',                      // CommonJS for VS Code extension host
    platform: 'node',
    target: 'node18',
    sourcemap: false,
    minify: false,                      // keep readable for debugging
    logLevel: 'info',
};

// Watch mode flag from command line.
const watch = process.argv.includes('--watch');

async function main() {
    if (watch) {
        const ctx = await esbuild.context(buildOptions);
        await ctx.watch();
        console.log('[esbuild] watching for changes...');
    } else {
        await esbuild.build(buildOptions);
        console.log('[esbuild] build complete.');
    }
}

main().catch((err) => {
    console.error(err);
    process.exit(1);
});
