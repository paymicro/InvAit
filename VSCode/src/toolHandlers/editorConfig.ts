/**
 * `.editorconfig` resolution for the VS Code host.
 *
 * Mirror of `ToolCore/EditorConfig.cs` (the hosts share no code): only `charset` and
 * `end_of_line` matter here, because those are the two properties that decide how a file is
 * WRITTEN. Everything else in the file is a formatter's concern.
 *
 * WHY THIS EXISTS: `createFile` used to call `fs.writeFileSync(path, content, 'utf-8')`, which
 * ignores the tree entirely. In a tree that declares `charset = utf-8-bom` (or a CRLF tree) the
 * written file silently violates the rule the whole repository is normalised to.
 *
 * SCOPE (deliberate, identical to the C# side): the supported glob subset is `*`, `**`, `?`,
 * `[...]`, `{a,b}`, `{1..9}`. A pattern with no `/` matches at any depth, per the spec. Matching
 * is case-insensitive. An unsupported construct simply does not match, which degrades to
 * "keep the caller's default" instead of guessing.
 *
 * `unset` REMOVES a property instead of being stored literally: it exists to clear a value
 * inherited from a parent `.editorconfig` (or from an earlier section of the same file). Storing it
 * made the writer report "charset 'unset' is not supported" on a perfectly valid tree.
 *
 * HARDENING: a `.editorconfig` arrives with the checked-out repository, so a hostile one must not
 * be able to hang the extension host. C# compiles every section with a 200 ms match timeout; JS
 * cannot interrupt a regex, so the equivalent guarantees are made earlier — a pattern-length cap,
 * collapsing the only repetition shapes this translator can nest, and turning a section that does
 * not compile into one that matches NOTHING instead of an exception out of the resolution walk.
 */

import * as fs from 'fs';
import * as path from 'path';

export interface EditorConfigSettings {
    /** Raw `charset` value: utf-8, utf-8-bom, latin1, utf-16le, utf-16be. Never 'unset'. */
    charset?: string;
    /** Raw `end_of_line` value: lf, crlf, cr. Never 'unset'. */
    endOfLine?: string;
}

/** Separator `end_of_line` asks for, or undefined when unset/unknown. */
export function resolveSeparator(settings: EditorConfigSettings): string | undefined {
    switch (settings.endOfLine) {
        case 'lf': return '\n';
        case 'crlf': return '\r\n';
        case 'cr': return '\r';
        default: return undefined;
    }
}

/** The only two properties this resolver collects; `unset` applies to both. */
const RESOLVED_KEYS = ['charset', 'end_of_line'] as const;

/** The value that REMOVES a property instead of setting one. */
const UNSET = 'unset';

interface Section {
    pattern: RegExp;
    properties: Record<string, string>;
}

interface ParsedConfig {
    isRoot: boolean;
    sections: Section[];
}

function parseConfig(text: string): ParsedConfig {
    const sections: Section[] = [];
    let isRoot = false;
    let current: Record<string, string> | null = null;

    for (const rawLine of text.split('\n')) {
        const line = rawLine.trim();
        if (line.length === 0 || line[0] === '#' || line[0] === ';') continue;

        if (line[0] === '[' && line[line.length - 1] === ']') {
            current = {};
            sections.push({ pattern: globToRegex(line.slice(1, -1).trim()), properties: current });
            continue;
        }

        const separator = line.indexOf('=');
        if (separator <= 0) continue;

        const key = line.slice(0, separator).trim().toLowerCase();
        const value = line.slice(separator + 1).trim().toLowerCase();
        if (value.length === 0) continue;

        if (current === null) {
            // Preamble: only `root` is meaningful there.
            if (key === 'root') isRoot = value === 'true';
            continue;
        }

        current[key] = value;
    }

    return { isRoot, sections };
}

/** Later matching sections win, per the spec. */
function resolveFor(config: ParsedConfig, relativePath: string): Record<string, string> {
    const result: Record<string, string> = {};
    for (const section of config.sections) {
        if (!section.pattern.test(relativePath)) continue;
        Object.assign(result, section.properties);
    }
    return result;
}

/** Longest glob accepted. Anything longer is neutralised instead of turned into a huge regex. */
const MAX_PATTERN_LENGTH = 512;

/**
 * Regex that matches nothing, used for a section that must be IGNORED: an over-long pattern, or one
 * whose generated source does not compile. C# gives every section a match timeout; JS has no way to
 * interrupt a `RegExp`, and throwing here would abort `.editorconfig` resolution for every file in
 * the workspace. "Cannot be run safely" therefore means "matches nothing".
 */
const NEVER_MATCHES: RegExp = new RegExp('(?!)');

/** `*` and `?` never cross `/`; `**` does; a pattern without `/` matches at any depth. */
export function globToRegex(pattern: string): RegExp {
    if (pattern.length > MAX_PATTERN_LENGTH) return NEVER_MATCHES;

    let source = pattern.replace(/\\/g, '/');
    if (!source.includes('/')) source = '**/' + source;

    let regex = '^';
    let lastToken = '';
    // `(?:.*/)?(?:.*/)?` IS `(?:.*/)?`, `[^/]*[^/]*` IS `[^/]*` and `.*.*` IS `.*`: repeating those
    // tokens adds nothing but backtracking paths, which is exactly how `[**/**/**/...*]` would
    // wedge a matcher. Both nesting shapes this translator can build are collapsed here; every
    // other quantifier comes from our own fixed token list or from a brace body, which is escaped
    // (so `{a+b}` stays literal).
    const emit = (token: string): void => {
        if (token === lastToken && (token === '(?:.*/)?' || token === '[^/]*' || token === '.*')) return;
        regex += token;
        lastToken = token;
    };

    // A cursor, not a `for` counter: this is a hand-written scanner, and every branch consumes a
    // DIFFERENT number of characters (`[abc]`, `{a,b}`, `***/`). Mutating a `for` counter in the
    // body made the loop's own `i++` a second, invisible advance that each branch had to compensate
    // for — and it is exactly what the `S2310` rule forbids, because a reader cannot tell from the
    // header how far the next iteration starts. Here every branch advances `i` exactly ONCE, past
    // what it consumed, so the invariant is local and visible.
    let i = 0;
    while (i < source.length) {
        const c = source[i];

        if (c === '*') {
            if (source[i + 1] === '*') {
                // A run of three or more stars is still "any depth"; collapse it so the pattern
                // length cannot translate into nested `. *` quantifiers. `end`, not `i`: the run is
                // measured first, so nothing inside this loop reassigns the cursor.
                let end = i;
                while (source[end + 1] === '*') end++;

                // `**/` matches ANY number of directories, including none: `**/*.cs` must match
                // `file.cs` as well as `src/deep/file.cs`. Without this the most common patterns
                // (`*.cs`, `*.{ts,tsx}`) match nothing in the directory that owns the .editorconfig.
                if (source[end + 1] === '/') {
                    emit('(?:.*/)?');
                    i = end + 2;
                } else {
                    emit('.*');
                    i = end + 1;
                }
            } else {
                emit('[^/]*');
                i++;
            }
        } else if (c === '?') {
            emit('[^/]');
            i++;
        } else if (c === '[') {
            const close = source.indexOf(']', i + 1);
            if (close < 0) {
                // An unclosed class is a literal `[`: consume the one character and carry on.
                emit('\\[');
                i++;
            } else {
                let body = source.slice(i + 1, close);
                if (body.startsWith('!')) body = '^' + body.slice(1);
                // An EMPTY class (`[]`, `[!]`) is not "any character": .NET rejects `[^]` as an
                // unterminated set and ignores the section, while JavaScript accepts it and matches
                // EVERYTHING — so `[*.[!]x]` silenced the section in the VS host and applied it here.
                // A header that cannot compile must contribute nothing in both hosts.
                if (body === '' || body === '^') return NEVER_MATCHES;
                emit('[' + body + ']');
                i = close + 1;
            }
        } else if (c === '{') {
            const braceEnd = source.indexOf('}', i + 1);
            if (braceEnd < 0) {
                // The same for `{`: a brace with no closing one is literal text.
                emit('\\{');
                i++;
            } else {
                emit('(?:' + braceAlternation(source.slice(i + 1, braceEnd)) + ')');
                i = braceEnd + 1;
            }
        } else {
            emit(c.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
            i++;
        }
    }

    try {
        return new RegExp(regex + '$', 'i');
    } catch {
        // A body the translator could not turn into a valid class (`[z-a]` out of a `file[z-a].cs`
        // section) is a section that matches nothing, not an exception.
        return NEVER_MATCHES;
    }
}

/** `{a,b}` → `a|b`; `{1..9}` → the expanded numbers (bounded, to keep the regex small). */
function braceAlternation(body: string): string {
    const range = numericRange(body);
    if (range !== null) return range;
    return body.split(',').map(part => part.trim().replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|');
}

function numericRange(body: string): string | null {
    const separator = body.indexOf('..');
    if (separator < 0) return null;

    // `Number('')` is 0 while `int.TryParse("")` fails, so a bare `{..9}` used to expand to
    // `0|1|…|9` here and stay a literal in C#. Both sides must be real integers, otherwise the
    // brace is not a range and falls back to the literal alternation (C# emits `{..9}` verbatim).
    const fromText = body.slice(0, separator).trim();
    const toText = body.slice(separator + 2).trim();
    if (!isInt32(fromText) || !isInt32(toText)) return null;

    const from = Number(fromText);
    const to = Number(toText);

    // A REVERSED range (`{9..1}`) addresses nothing at all, so its alternation must match NOTHING.
    // Sharing the fallback below made `[file{9..1}.cs]` apply its properties to EVERY numbered
    // file — the exact opposite of "a header that does not match anything matches nothing", and
    // the opposite of what `ToolCore/EditorConfig.cs` emits, so one tree gave two charsets.
    // `(?!)` — an empty negative lookahead — can never match, and is the closest JS equivalent of
    // the `(?!)` the C# twin returns.
    if (to < from) return '(?!)';

    // Ascending but too wide to expand: the DOCUMENTED over-approximation — any run of digits. That
    // is a deliberate trade (one small token instead of 1000 alternatives), not an accident, and it
    // is now reached ONLY for a range that is genuinely too large.
    if (to - from > 100) return '[0-9]+';

    const values: string[] = [];
    for (let value = from; value <= to; value++) values.push(String(value));
    return values.join('|');
}

/** `int.TryParse` parity: optional sign, digits only, inside Int32, so `{1..999999999999}` is not a range. */
function isInt32(text: string): boolean {
    if (!/^[+-]?\d+$/.test(text)) return false;
    const value = Number(text);
    return Number.isSafeInteger(value) && Math.abs(value) <= 2147483648;
}

/**
 * Walks `.editorconfig` files from the file's directory up to the filesystem root,
 * stopping at `root = true`. Closest file wins per property.
 */
export function resolveEditorConfig(filePath: string): EditorConfigSettings {
    const fullPath = path.resolve(filePath);
    const chain: { directory: string; config: ParsedConfig }[] = [];

    let directory = path.dirname(fullPath);
    while (directory) {
        const configPath = path.join(directory, '.editorconfig');
        if (fs.existsSync(configPath)) {
            // A config that cannot be READ contributes nothing, exactly like one whose glob does not
            // compile. Without this the walk threw out of `resolveEditorConfig` on a locked file,
            // restrictive ACLs or a DIRECTORY named `.editorconfig` (`existsSync` is true for a
            // directory, `readFileSync` then throws EISDIR) — and every write in that tree failed.
            let config: ParsedConfig | undefined;
            try {
                config = parseConfig(String(fs.readFileSync(configPath, 'utf-8')));
            } catch {
                config = undefined;
            }

            if (config !== undefined) {
                chain.push({ directory, config });
                if (config.isRoot) break;
            }
        }

        const parent = path.dirname(directory);
        // Guard against a path that has no parent (drive root) looping forever.
        if (!parent || parent === directory) break;
        directory = parent;
    }

    const resolved: Record<string, string | undefined> = {};

    // The chain is built nearest-first; apply farthest-first so a closer file always wins.
    for (let i = chain.length - 1; i >= 0; i--) {
        const { directory: configDirectory, config } = chain[i];
        const relativePath = path.relative(configDirectory, fullPath);
        if (!isInside(relativePath)) continue;

        const properties: Record<string, string | undefined> = resolveFor(config, relativePath.split(path.sep).join('/'));
        for (const key of RESOLVED_KEYS) {
            const value = properties[key];
            if (value === undefined) continue;
            // `unset` REMOVES the key, so it clears a value inherited from a parent file — the
            // whole point of the keyword. Storing it made the writer report "charset 'unset' is
            // not supported" on a tree that is perfectly valid.
            resolved[key] = value === UNSET ? undefined : value;
        }
    }

    const settings: EditorConfigSettings = {};
    const charset = resolved['charset'];
    const endOfLine = resolved['end_of_line'];
    if (charset !== undefined) settings.charset = charset;
    if (endOfLine !== undefined) settings.endOfLine = endOfLine;
    return settings;
}

/**
 * True when `relativePath` stays inside the directory that owns the `.editorconfig`.
 *
 * The guard covers a file on another drive (Windows `path.relative` then returns a rooted path) and
 * a parent link. It must NOT test `startsWith('..')`: that also throws away a real file named
 * `..foo`. It also has to run BEFORE the path is normalised to '/', because `path.sep` is '\\' on
 * Windows and the normalised string no longer contains it.
 */
function isInside(relativePath: string): boolean {
    if (path.isAbsolute(relativePath)) return false;
    if (relativePath === '..') return false;
    if (relativePath.startsWith('..' + path.sep)) return false;
    return true;
}
