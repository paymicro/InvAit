/**
 * Unit tests for the bash output sanitizer (toolHandlers/bashOutput.ts).
 *
 * Mirror of ToolCore.Tests/BashOutputSanitizerTests.cs. Both halves of the rule are pinned:
 * our own temp file disappears, the user's own script keeps its name.
 */
import * as chai from 'chai';
import { sanitizeBashOutput } from '../../toolHandlers/bashOutput';

const expect = chai.expect;

const TEMP_PATH = 'C:\\Users\\pc\\AppData\\Local\\Temp\\invait_8f45813bcf3141d0841baff67db24fae.sh';

describe('sanitizeBashOutput', () => {
    it('should remove the exact temp path and keep the message', () => {
        const output = `${TEMP_PATH}: line 1: strings: command not found`;

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('line 1: strings: command not found');
    });

    it('should remove the temp path even when no path is passed in', () => {
        // The name pattern alone must suffice: a caller that forgets to pass the path must not
        // resurrect the noise.
        const output = `${TEMP_PATH}: line 1: strings: command not found`;

        expect(sanitizeBashOutput(output, undefined)).to.equal('line 1: strings: command not found');
    });

    it('should remove the forward-slash variant', () => {
        const output = 'C:/Users/pc/AppData/Local/Temp/invait_8f45813bcf3141d0841baff67db24fae.sh: line 2: cd: /nope: No such file or directory';

        expect(sanitizeBashOutput(output, TEMP_PATH))
            .to.equal('line 2: cd: /nope: No such file or directory');
    });

    it('should remove a temp path whose directory contains spaces', () => {
        // Not covered by the name pattern — this is why the exact path is passed in as well.
        const tempPath = 'C:\\Users\\John Doe\\AppData\\Local\\Temp\\invait_8f45813bcf3141d0841baff67db24fae.sh';
        const output = `${tempPath}: line 1: syntax error near unexpected token`;

        expect(sanitizeBashOutput(output, tempPath))
            .to.equal('line 1: syntax error near unexpected token');
    });

    it('should keep the name of the user own script', () => {
        // Stripping by identity, never by shape: a real script's name is the useful part.
        const output = 'build.sh: line 3: dotnet: command not found';

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('build.sh: line 3: dotnet: command not found');
    });

    it('should keep the absolute path of the user own script', () => {
        const output = '/home/pc/project/scripts/deploy.sh: line 7: kubectl: command not found';

        expect(sanitizeBashOutput(output, TEMP_PATH))
            .to.equal('/home/pc/project/scripts/deploy.sh: line 7: kubectl: command not found');
    });

    it('should remove every occurrence', () => {
        const output = [
            `${TEMP_PATH}: line 1: strings: command not found`,
            'some output',
            `${TEMP_PATH}: line 2: wc: command not found`,
        ].join('\n');

        expect(sanitizeBashOutput(output, TEMP_PATH))
            .to.equal('line 1: strings: command not found\nsome output\nline 2: wc: command not found');
    });

    it('should drop a line that became empty', () => {
        // `echo $0` prints nothing but the path; an empty line would be our own noise.
        const output = `${TEMP_PATH}\nreal output`;

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('real output');
    });

    it('should keep lines that were empty already', () => {
        const output = `first\n\n${TEMP_PATH}: line 1: boom\nlast`;

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('first\n\nline 1: boom\nlast');
    });

    it('should not glue neighbouring lines together', () => {
        // `\s?` in the pattern would eat the newline and merge the two lines.
        const output = `${TEMP_PATH}: line 1: boom\nnext line`;

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('line 1: boom\nnext line');
    });

    it('should remove a quoted path', () => {
        const output = `'${TEMP_PATH}': line 1: boom`;

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('line 1: boom');
    });

    it('should leave unrelated output untouched', () => {
        const output = 'Build succeeded.\n0 Warning(s)\n0 Error(s)';

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal(output);
    });

    it('should return an empty string for missing output', () => {
        expect(sanitizeBashOutput(undefined, TEMP_PATH)).to.equal('');
        expect(sanitizeBashOutput('', TEMP_PATH)).to.equal('');
    });

    it('should keep a file name that merely ends with our prefix', () => {
        // The path prefix must end with a separator and the match must start on a boundary.
        // Without both, the `my` tail landed in the path class and `myinvait_abc.sh` lost its name
        // — the sanitizer destroying exactly the information it exists to protect.
        for (const output of [
            'myinvait_abc.sh: line 1: boom',
            'my-invait_abc.sh: line 1: boom',
            'xinvait_abc.sh: boom',
        ]) {
            expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal(output);
        }
    });

    it('should remove an upper-case extension', () => {
        // Windows paths are case-insensitive, and a shell may upper-case the name it echoes.
        const output = 'c:\\temp\\INVAIT_8F45813BCF3141D0841BAFF67DB24FAE.SH: line 1: boom';

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('line 1: boom');
    });

    it('should remove a bare file name without a directory', () => {
        // `sh` prints the path as it was given; a relative invocation yields no directory part.
        const output = 'invait_8f45813bcf3141d0841baff67db24fae.sh: line 1: boom';

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('line 1: boom');
    });

    it('should remove the C# host file name shape', () => {
        // The C# host names the file invait_<32 hex>.sh; one rule must cover both channels.
        const output = 'C:\\Temp\\invait_8f45813bcf3141d0841baff67db24fae.sh: line 1: strings: command not found';

        expect(sanitizeBashOutput(output, undefined)).to.equal('line 1: strings: command not found');
    });

    it('should be reusable across calls despite the stateful global regex', () => {
        // A /g regex carries lastIndex between calls; a missing reset would make the second call
        // silently skip the match.
        const output = `${TEMP_PATH}: line 1: boom`;

        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('line 1: boom');
        expect(sanitizeBashOutput(output, TEMP_PATH)).to.equal('line 1: boom');
    });
});
