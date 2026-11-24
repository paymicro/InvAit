/**
 * Strips the temp-script path out of bash stdout/stderr.
 *
 * Mirror of `ToolCore/BashOutputSanitizer.cs` (the Node host shares no code with the C# one,
 * so the rule and the file-name shape are duplicated here with a pointer back). Fixing one
 * side without the other puts the bug back into that channel.
 *
 * `sh` prefixes every diagnostic with the script file name:
 *   `C:\Users\pc\AppData\Local\Temp\invait_8f45....sh: line 1: strings: command not found`
 * The model cannot use that path — the file is deleted in `finally` right after the run — and
 * the prefix eats context. Only the useful part survives: `line 1: strings: command not found`.
 *
 * The rule strips by IDENTITY, never by shape: our own temp file (the exact path we handed to
 * `sh`, plus the `invait_<...>.sh` name pattern) is removed, but never "any .sh". Otherwise an
 * error in the user's own `build.sh` would lose its file name — the sanitizer would destroy the
 * very information the message exists to carry.
 */

/**
 * Name shape of the temp script created by `bash()`.
 *
 * The character class includes `:` and the path separators so the match starts at the drive
 * letter (`C:\...`) instead of the first directory — otherwise a `C:\` fragment would survive.
 * The trailing `:` is eaten with the path because it belongs to the `file: message` format,
 * not to the message. `[ \t]?` instead of `\s?` is deliberate: `\s` would swallow a newline
 * and glue neighbouring output lines together.
 *
 * The path prefix must end with a separator and the match must start on a boundary
 * (`(?<![\w.:/\\-])`). Without both, the pattern fired on the user's own file
 * `myinvait_abc.sh`: the `my` tail landed in `[\w.:/\\-]*` and the file name was lost — the
 * sanitizer destroying exactly the information it exists to protect.
 *
 * The tail (`[\w-]+`) does not pin the GUID format: the Node host names the file
 * `invait_<ms>_<rand>.sh`, the C# host `invait_<32 hex>.sh`, and the rule must be one for both.
 *
 * Accepted cost: a user file named `invait_*.sh` loses its name too. The `invait_` prefix is our
 * reserved marker, so such a file counts as ours.
 */
const TEMP_SCRIPT_NAME = /(?<![\w.:/\\-])['"]?(?:[\w.:/\\-]*[/\\])?invait_[\w-]+\.sh['"]?(:[ \t]?)?/gi;

/** Escapes a literal string for use inside a RegExp. */
function escapeRegExp(value: string): string {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Removes the exact path we handed to `sh`, plus its forward-slash variant.
 * Needed for temp directories containing spaces, which the name pattern cannot cover whole.
 */
function replaceExactPath(output: string, tempScriptPath: string | undefined): string {
    if (!tempScriptPath) return output;

    const variants = [tempScriptPath];
    const forwardSlash = tempScriptPath.replace(/\\/g, '/');
    if (forwardSlash !== tempScriptPath) variants.push(forwardSlash);

    // Longest first: a shorter variant would otherwise eat part of a longer one.
    variants.sort((a, b) => b.length - a.length);

    const pattern = new RegExp(
        `['"]?(?:${variants.map(escapeRegExp).join('|')})['"]?(:[ \\t]?)?`,
        'gi' // Windows paths are case-insensitive and the shell may change the drive-letter case
    );
    return output.replace(pattern, '');
}

/**
 * Drops lines that became empty ONLY because the path was cut out (e.g. the output of `echo $0`).
 * Lines that were empty to begin with stay: they are part of the command's output, not our noise.
 */
function dropEmptiedLines(original: string, cleaned: string): string {
    if (original.length === cleaned.length) return cleaned;

    const originalLines = original.split('\n');
    const cleanedLines = cleaned.split('\n');
    if (originalLines.length !== cleanedLines.length) return cleaned;

    const kept: string[] = [];
    for (let i = 0; i < cleanedLines.length; i++) {
        if (cleanedLines[i].trim().length === 0 && originalLines[i].trim().length > 0) continue;
        kept.push(cleanedLines[i]);
    }
    return kept.join('\n');
}

/**
 * Strips references to the temp script from a bash output stream.
 *
 * @param output stdout or stderr of the process.
 * @param tempScriptPath the path passed to `sh` (used for paths containing spaces).
 */
export function sanitizeBashOutput(output: string | undefined, tempScriptPath: string | undefined): string {
    if (!output) return '';

    const exactStripped = replaceExactPath(output, tempScriptPath);
    // `lastIndex` is stateful on a /g regex — reset before every use.
    TEMP_SCRIPT_NAME.lastIndex = 0;
    const cleaned = exactStripped.replace(TEMP_SCRIPT_NAME, '');

    return dropEmptiedLines(output, cleaned);
}
