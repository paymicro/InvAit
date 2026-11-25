/**
 * Unit tests for `.editorconfig` resolution and its glob translator (toolHandlers/editorConfig.ts).
 *
 * The fs is driven through a PER-PATH stub map, so a test can describe a whole directory tree. That
 * matters: the walk used to be "covered" by returning the same config from every directory, which
 * meant the first parsed section already contained `root = true` and the loop broke on iteration 1 —
 * the test proved nothing about inheritance, precedence or the root marker.
 */
import * as chai from 'chai';
import * as sinon from 'sinon';
import * as path from 'path';
import { fsStub, resetStubs, stubFsTree } from './setup';
import { globToRegex, resolveEditorConfig } from '../../toolHandlers/editorConfig';

const expect = chai.expect;

const ROOT = path.resolve('fake-tree-root');

describe('editorConfig globToRegex', () => {
    it('should match a bare extension pattern at the owning directory and any depth', () => {
        const re = globToRegex('*.cs');
        expect(re.test('file.cs')).to.be.true;
        expect(re.test('src/file.cs')).to.be.true;
        expect(re.test('src/deep/file.cs')).to.be.true;
        expect(re.test('file.ts')).to.be.false;
    });

    it('should match a brace alternation', () => {
        const re = globToRegex('*.{cs,razor}');
        expect(re.test('a.cs')).to.be.true;
        expect(re.test('a.razor')).to.be.true;
        expect(re.test('a.ts')).to.be.false;
    });

    it('should keep a pattern with a slash anchored to its directory', () => {
        const re = globToRegex('src/**/*.cs');
        expect(re.test('src/a.cs')).to.be.true;
        expect(re.test('src/a/b/c.cs')).to.be.true;
        expect(re.test('other/a.cs')).to.be.false;
    });

    it('should not let a single star cross a directory separator', () => {
        const re = globToRegex('src/*.cs');
        expect(re.test('src/a.cs')).to.be.true;
        expect(re.test('src/deep/a.cs')).to.be.false;
    });

    it('should match any single character for a question mark', () => {
        const re = globToRegex('file?.cs');
        expect(re.test('file1.cs')).to.be.true;
        expect(re.test('file.cs')).to.be.false;
    });

    it('should support character classes', () => {
        const re = globToRegex('file[0-9].cs');
        expect(re.test('file7.cs')).to.be.true;
        expect(re.test('filex.cs')).to.be.false;
    });

    it('should support a negated character class', () => {
        const re = globToRegex('file[!0-9].cs');
        expect(re.test('filex.cs')).to.be.true;
        expect(re.test('file7.cs')).to.be.false;
    });

    it('should read a backslash-separated pattern as directories', () => {
        const re = globToRegex('src\\*.cs');
        expect(re.test('src/a.cs')).to.be.true;
        expect(re.test('src/deep/a.cs')).to.be.false;
        expect(re.test('other/a.cs')).to.be.false;
    });

    it('should expand a numeric range', () => {
        const re = globToRegex('file{1..3}.cs');
        expect(re.test('file2.cs')).to.be.true;
        expect(re.test('file4.cs')).to.be.false;
    });

    it('should match nothing for a REVERSED numeric range', () => {
        // `{9..1}` addresses no file at all, so its alternation must match nothing. It used to share
        // the too-wide fallback below and became `[0-9]+`, which applied the section to every
        // numbered file here while the C# twin (which emits `(?!)`) matched none: one tree, two
        // charsets. `(?!)` is a lookahead that can never succeed — the closest JS equivalent.
        const re = globToRegex('file{9..1}.cs');
        expect(re.test('file5.cs')).to.be.false;
        expect(re.test('file9.cs')).to.be.false;
        expect(re.test('file1.cs')).to.be.false;
    });

    it('should keep the over-approximation for a range that is merely too wide', () => {
        // Past 100 alternatives the trade is one `[0-9]+` token instead of a thousand — documented
        // and deliberate, and now reached ONLY for an ascending range that is genuinely too large.
        const re = globToRegex('file{1..500}.cs');
        expect(re.test('file77.cs')).to.be.true;
        expect(re.test('file500.cs')).to.be.true;
        expect(re.test('file.cs')).to.be.false;
        expect(re.test('filex.cs')).to.be.false;
    });

    it('should keep a range with an empty side literal instead of starting at zero', () => {
        // `Number('')` is 0 in JS and `int.TryParse("")` fails in C#, so `{..9}` used to expand to
        // `0|1|…|9` here while staying literal there. Both hosts must emit the literal.
        const re = globToRegex('file{..9}.cs');
        expect(re.test('file9.cs')).to.be.false;
        expect(re.test('file..9.cs')).to.be.true;
    });

    it('should treat a dot literally', () => {
        const re = globToRegex('*.cs');
        expect(re.test('fileXcs')).to.be.false;
    });

    it('should match case-insensitively', () => {
        const re = globToRegex('*.CS');
        expect(re.test('file.cs')).to.be.true;
    });

    it('should match nothing for a section whose glob cannot compile', () => {
        // `[z-a]` is a valid glob to read but an invalid character class: the section must be ignored
        // rather than throwing out of the resolution walk inside the extension host.
        const re = globToRegex('file[z-a].cs');
        expect(re.test('filez.cs')).to.be.false;
        expect(re.test('filea.cs')).to.be.false;
    });

    it('should match nothing for an EMPTY character class instead of everything', () => {
        // `[!]` becomes `[^]`, which JavaScript accepts as "any character" while .NET rejects it as
        // an unterminated set. Left alone, `[*.[!]x]` silenced the section in the VS host and applied
        // its charset to every file here — the same header, two different trees.
        for (const pattern of ['file[]', 'file[!]']) {
            const re = globToRegex(pattern);
            expect(re.test('filea'), pattern).to.be.false;
            expect(re.test('file.cs'), pattern).to.be.false;
        }
    });

    it('should neutralise an absurdly long pattern instead of compiling it', () => {
        const re = globToRegex('*'.repeat(600));
        expect(re.test('anything.cs')).to.be.false;
    });

    it('should collapse repeated any-depth groups', () => {
        // `(?:.*/)?(?:.*/)?` is `(?:.*/)?`: keeping the duplicates only multiplies backtracking.
        const re = globToRegex('**/'.repeat(100) + '*.cs');
        expect(re.test('a/b/c.cs')).to.be.true;
        expect(re.test('c.ts')).to.be.false;
    });
});

describe('editorConfig resolveEditorConfig', () => {
    afterEach(() => resetStubs());

    it('should take every property from the closest file that sets it', () => {
        stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '[*]\ncharset = utf-8-bom\n',
            [path.join(ROOT, 'sub', '.editorconfig')]: '[*]\nend_of_line = crlf\n',
        });

        const settings = resolveEditorConfig(path.join(ROOT, 'sub', 'a.cs'));

        // Inheritance is PER PROPERTY: the far file supplies charset, the near one line endings.
        expect(settings.charset).to.equal('utf-8-bom');
        expect(settings.endOfLine).to.equal('crlf');
    });

    it('should stop the walk at root = true instead of inheriting from the parent', () => {
        const tree = stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '[*]\ncharset = latin1\nend_of_line = crlf\n',
            [path.join(ROOT, 'sub', '.editorconfig')]: 'root = true\n[*]\nend_of_line = lf\n',
        });

        const settings = resolveEditorConfig(path.join(ROOT, 'sub', 'deep', 'a.cs'));

        expect(settings.charset).to.be.undefined;
        expect(settings.endOfLine).to.equal('lf');
        // The marker must STOP the walk, not merely lose a precedence race: the parent file is never read.
        expect(tree.reads).to.not.include(path.join(ROOT, '.editorconfig'));
    });

    it('should let a later matching section win over an earlier one', () => {
        stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '[*]\ncharset = utf-8\nend_of_line = crlf\n[*.cs]\ncharset = utf-16le\n',
        });

        const settings = resolveEditorConfig(path.join(ROOT, 'a.cs'));

        expect(settings.charset).to.equal('utf-16le');
        expect(settings.endOfLine).to.equal('crlf');
    });

    it('should treat charset = unset as removing a value inherited from a parent file', () => {
        stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '[*]\ncharset = utf-8-bom\nend_of_line = crlf\n',
            [path.join(ROOT, 'sub', '.editorconfig')]: '[*.cs]\ncharset = unset\nend_of_line = unset\n',
        });

        const settings = resolveEditorConfig(path.join(ROOT, 'sub', 'a.cs'));

        // Never the literal string 'unset': the writer would report an unsupported charset on a
        // perfectly valid tree.
        expect(settings.charset).to.be.undefined;
        expect(settings.endOfLine).to.be.undefined;
    });

    it('should treat charset = unset as removing the value set earlier in the same file', () => {
        stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '[*]\ncharset = latin1\n[*.ts]\ncharset = unset\n',
        });

        expect(resolveEditorConfig(path.join(ROOT, 'a.ts')).charset).to.be.undefined;
        expect(resolveEditorConfig(path.join(ROOT, 'a.cs')).charset).to.equal('latin1');
    });

    it('should not resolve settings from a section whose numeric range is reversed', () => {
        stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '[file{9..1}.cs]\ncharset = latin1\n[*]\nend_of_line = crlf\n',
        });

        // file5.cs is exactly the file the old over-approximation matched. Only the second section
        // may apply: a header that cannot match anything contributes nothing.
        const settings = resolveEditorConfig(path.join(ROOT, 'file5.cs'));

        expect(settings.charset).to.be.undefined;
        expect(settings.endOfLine).to.equal('crlf');

        // The LITERAL name too: deleting the reversed-range branch leaves an EMPTY alternation,
        // `file(?:\.cs)`, which still misses file5.cs but DOES match file.cs — without this line the
        // guard could be removed with every assertion above still green.
        expect(resolveEditorConfig(path.join(ROOT, 'file.cs')).charset).to.be.undefined;
    });

    it('should ignore an uncompilable section instead of throwing', () => {
        stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '[file[z-a].cs]\ncharset = latin1\n[*]\nend_of_line = crlf\n',
        });

        const settings = resolveEditorConfig(path.join(ROOT, 'filez.cs'));

        expect(settings.charset).to.be.undefined;
        expect(settings.endOfLine).to.equal('crlf');
    });

    it('should resolve a file inside a directory whose name starts with two dots', () => {
        stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '[*]\ncharset = latin1\n',
        });

        // `relative` here is `..config/a.cs`. A `startsWith('..')` guard threw that away even though
        // it is a real path inside the tree, not a parent link.
        const settings = resolveEditorConfig(path.join(ROOT, '..config', 'a.cs'));

        expect(settings.charset).to.equal('latin1');
    });

    it('should return an empty result when no .editorconfig applies', () => {
        stubFsTree({});

        const settings = resolveEditorConfig(path.join(ROOT, 'a.cs'));

        expect(settings.charset).to.be.undefined;
        expect(settings.endOfLine).to.be.undefined;
    });

    it('should ignore an .editorconfig that cannot be read instead of throwing', () => {
        // The walk sees the path, the read fails — a locked file, ACLs, a network share, or a
        // DIRECTORY named `.editorconfig` (existsSync is true for a directory, readFileSync throws
        // EISDIR). The promise in the module header is that a hostile or broken config must not
        // break the write; it was kept only for the glob half.
        stubFsTree({});
        (fsStub.existsSync as sinon.SinonStub).returns(true);
        (fsStub.readFileSync as sinon.SinonStub).throws(
            Object.assign(new Error("EISDIR: illegal operation on a directory, read"), { code: 'EISDIR' }));

        const settings = resolveEditorConfig(path.join(ROOT, 'a.cs'));

        expect(settings.charset).to.be.undefined;
        expect(settings.endOfLine).to.be.undefined;
    });

    it('should resolve the first section of an .editorconfig saved with a BOM', () => {
        // Visual Studio and Notepad save `.editorconfig` WITH a BOM by default on Windows, and
        // `readFileSync(path, 'utf-8')` does NOT strip it — the first character of the first line is
        // U+FEFF. This host depends on `line.trim()` (ECMAScript WhiteSpace includes ZWNBSP) to
        // remove it; without that the first header is misread as a property line and every setting
        // in the file disappears, silently, because the write falls back to its default encoding.
        stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '\uFEFF[*]\ncharset = latin1\n',
        });

        expect(resolveEditorConfig(path.join(ROOT, 'a.cs')).charset).to.equal('latin1');
    });

    it('should skip an unparseable line without ending the section it appears in', () => {
        // A hand-edited config carries junk: a stray word, a header missing its `]`, a property with
        // no value. Each must be skipped WITHOUT resetting the current section — otherwise every
        // setting below the junk line is lost and the file is written with defaults.
        stubFsTree({
            [path.join(ROOT, '.editorconfig')]: '[*]\nthis line is junk\n[*\ncharset =\ncharset = latin1\n',
        });

        expect(resolveEditorConfig(path.join(ROOT, 'a.cs')).charset).to.equal('latin1');
    });
});
