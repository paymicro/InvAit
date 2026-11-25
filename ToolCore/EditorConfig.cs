using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace ToolCore;

/// <summary>
/// The two `.editorconfig` properties that change how a file must be WRITTEN.
/// Everything else in the file (indent size, naming rules) is a formatter concern, not a writer concern.
/// <para>
/// INTERNAL on purpose: this is a ToolCore implementation detail. Its only consumers are
/// <see cref="FileWriter"/> and <see cref="FileUtils"/>, and a public `IFileSystem`-injectable
/// resolver is an API someone would build on before it is one we want to support.
/// </para>
/// </summary>
internal sealed class EditorConfigSettings
{
    public static EditorConfigSettings Empty { get; } = new(null, null);

    public EditorConfigSettings(string? charset, string? endOfLine)
    {
        Charset = charset;
        EndOfLine = endOfLine;
    }

    /// <summary>
    /// Raw `charset` value: utf-8, utf-8-bom, latin1, utf-16le, utf-16be. null when unset — and
    /// a `charset = unset` line REMOVES the property, including one inherited from a parent, so the
    /// literal "unset" never reaches the writer. It used to be stored as a value and printed back
    /// as "charset 'unset' is not supported", which is nonsense on a perfectly valid tree.
    /// </summary>
    public string? Charset { get; }

    /// <summary>Raw `end_of_line` value: lf, crlf, cr. null when unset.</summary>
    public string? EndOfLine { get; }

    /// <summary>The separator `end_of_line` asks for, or null when unset/unknown.</summary>
    public string? Separator => EndOfLine switch
    {
        "lf" => "\n",
        "crlf" => "\r\n",
        "cr" => "\r",
        _ => null
    };

    /// <summary>
    /// Encoding `charset` asks for, or null when unset/unknown. An unknown value must NOT be
    /// guessed: the caller falls back to its own default and says so in the tool response.
    /// The TABLE lives in <see cref="TextFileCodec.CreateEncoding"/> and is only delegated to here:
    /// `create_file`, `edit_file` and the `.editorconfig` tests must not be able to disagree about
    /// what a charset name means.
    /// </summary>
    public Encoding? CreateEncoding() => TextFileCodec.CreateEncoding(Charset);

    public bool IsEmpty => Charset == null && EndOfLine == null;
}

/// <summary>
/// Resolves the effective `charset` / `end_of_line` for a file by walking `.editorconfig` files
/// from the file's directory up to the filesystem root, stopping at `root = true`.
///
/// WHY THIS EXISTS: `create_file` used to hard-code UTF-8 WITH a BOM. In a tree that declares
/// `charset = utf-8` (no BOM) that produced invisible diffs — the byte prefix shows up in git as a
/// changed first line and in C# as a stray U+FEFF at the start of the first token.
///
/// SCOPE (deliberate): only `charset` and `end_of_line` are collected, and only one glob subset is
/// supported — `*`, `**`, `?`, `[...]`, `{a,b}`, `{1..9}`. A pattern with no `/` matches at any
/// depth, per the spec. Case-insensitive matching is used (the hosts are Windows-first).
/// Unsupported constructs simply do not match, which degrades to "keep the caller's default" —
/// and a header that does not COMPILE (bad class, reversed range) matches nothing as well instead
/// of throwing out of <see cref="Resolve"/>, because one rotten `.editorconfig` somewhere above the
/// tree must not break every write in it.
/// The value `unset` REMOVES the property (clearing an inherited one), for both supported keys.
/// </summary>
internal sealed class EditorConfigResolver
{
    /// <summary>
    /// The `.editorconfig` keyword that REMOVES a property: the spec means "reset to the tool
    /// default", so it deletes the key instead of being stored as a literal value.
    /// </summary>
    private const string Unset = "unset";

    private readonly IFileSystem _fs;

    public EditorConfigResolver(IFileSystem? fs = null) => _fs = fs ?? new FileSystem();

    /// <param name="filePath">Absolute path of the file that is about to be written.</param>
    public EditorConfigSettings Resolve(string filePath)
    {
        var fullPath = _fs.Path.GetFullPath(filePath);
        var chain = new List<(string Directory, EditorConfigFile Config)>();

        var directory = _fs.Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(directory))
        {
            var configPath = _fs.Path.Combine(directory!, ".editorconfig");
            if (_fs.File.Exists(configPath))
            {
                // A config that cannot be READ contributes nothing, exactly like one whose pattern
                // does not compile. Without this the class doc's own promise broke on the other
                // half: `ReadAllText` threw out of `Resolve` (a file held by another process, ACLs,
                // a network share that went away) and EVERY `create_file` / `edit_file` under that
                // directory failed with an I/O message that names neither the tree nor the tool.
                EditorConfigFile? config = null;
                try
                {
                    config = EditorConfigFile.Parse(_fs.File.ReadAllText(configPath));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
                {
                    // Ignored on purpose: the write path must never fail because of the tree file.
                }

                if (config != null)
                {
                    chain.Add((directory!, config));
                    if (config.IsRoot)
                        break;
                }
            }

            var parent = _fs.Path.GetDirectoryName(directory);
            // Guard against a path that has no parent (drive root) looping forever.
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                break;
            directory = parent;
        }

        string? charset = null;
        string? endOfLine = null;

        // The chain is built nearest-first; apply farthest-first so a closer file always wins.
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var (configDirectory, config) = chain[i];
            var relative = RelativePath(configDirectory, fullPath);
            if (relative == null)
                continue;

            var properties = config.ResolveFor(relative);
            // `unset` CLEARS a value instead of being one: a closer file that says
            // `charset = unset` must win over `charset = latin1` in a parent config.
            if (properties.TryGetValue("charset", out var value))
                charset = value == Unset ? null : value;
            if (properties.TryGetValue("end_of_line", out var lineEnding))
                endOfLine = lineEnding == Unset ? null : lineEnding;
        }

        return charset == null && endOfLine == null ? EditorConfigSettings.Empty : new EditorConfigSettings(charset, endOfLine);
    }

    /// <summary>
    /// Path of <paramref name="fullPath"/> relative to <paramref name="directory"/>, '/'-separated.
    /// Paths go through `_fs.Path` and NOT `System.IO.Path`: a resolver that mixes the injected
    /// filesystem abstraction with the real host one answers questions about a tree it never read.
    /// </summary>
    private string? RelativePath(string directory, string fullPath)
    {
        var separatorChar = _fs.Path.DirectorySeparatorChar;
        var altChar = _fs.Path.AltDirectorySeparatorChar;
        var root = _fs.Path.GetFullPath(directory).TrimEnd(separatorChar, altChar);
        var target = _fs.Path.GetFullPath(fullPath);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;

        // A bare prefix test would place `C:\repo2\x` INSIDE `C:\repo`: the character right after
        // the root has to be a separator.
        if (target.Length > root.Length && target[root.Length] != separatorChar && target[root.Length] != altChar)
            return null;

        var relative = target.Substring(root.Length).TrimStart(separatorChar, altChar);
        return relative.Replace(separatorChar, '/').Replace(altChar, '/');
    }

    /// <summary>One parsed `.editorconfig` file: a preamble flag plus ordered sections.</summary>
    private sealed class EditorConfigFile
    {
        // A null pattern is a header that could not be translated: it matches NOTHING (see TryToRegex).
        private readonly List<(Regex? Pattern, Dictionary<string, string> Properties)> _sections = [];

        public bool IsRoot { get; private set; }

        public static EditorConfigFile Parse(string text)
        {
            var file = new EditorConfigFile();
            Dictionary<string, string>? current = null;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    // A header that cannot be translated matches NOTHING. One broken header in a
                    // user-wide .editorconfig used to throw out of Resolve and break EVERY write in
                    // the tree, which is a far worse outcome than ignoring that section.
                    file._sections.Add((TryToRegex(line.Substring(1, line.Length - 2).Trim()), current));
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                var value = line.Substring(separator + 1).Trim().ToLowerInvariant();
                if (value.Length == 0)
                    continue;

                if (current == null)
                {
                    // Preamble: only `root` is meaningful there.
                    if (key == "root")
                        file.IsRoot = value == "true";
                    continue;
                }

                current[key] = value;
            }

            return file;
        }

        /// <summary>Effective properties for a relative path: later matching sections win.</summary>
        public Dictionary<string, string> ResolveFor(string relativePath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pattern, properties) in _sections)
            {
                if (pattern == null || !Matches(pattern, relativePath))
                    continue;
                foreach (var pair in properties)
                {
                    // Kept verbatim, `unset` included: a later matching section must be able to
                    // CLEAR what an earlier one set, and Resolve turns `unset` into "absent".
                    result[pair.Key] = pair.Value;
                }
            }
            return result;
        }

        /// <summary>
        /// A pathological pattern can burn the match timeout; that is "this section does not apply",
        /// never an exception that aborts the write.
        /// </summary>
        private static bool Matches(Regex pattern, string relativePath)
        {
            try
            {
                return pattern.IsMatch(relativePath);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        /// <summary>
        /// <see cref="ToRegex"/> as a "may fail" value: a header the translator cannot express
        /// (e.g. `[*.[!]x]`) yields null, which matches nothing.
        /// </summary>
        private static Regex? TryToRegex(string pattern)
        {
            try
            {
                return ToRegex(pattern);
            }
            catch (ArgumentException)
            {
                // Malformed character class or range: invalid editorconfig glob syntax.
                return null;
            }
        }

        /// <summary>Bound on one match attempt, so a pathological pattern cannot hang a write.</summary>
        private static readonly TimeSpan _matchTimeout = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Translates an editorconfig glob into a regex.
        /// `*` and `?` never cross `/`; `**` does; a pattern without `/` is anchored to match at ANY depth.
        /// Only the wildcard, character-class and brace forms are special: every other character
        /// may appear literally in a pattern and is escaped by the default branch.
        /// </summary>
        private static Regex ToRegex(string pattern)
        {
            pattern = pattern.Replace('\\', '/');
            if (!pattern.Contains("/"))
                pattern = "**/" + pattern;

            var regex = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                switch (c)
                {
                    case '*':
                        if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                        {
                            i++;
                            // `**/` matches ANY number of directories, including none: `**/*.cs`
                            // must match `file.cs` as well as `src/deep/file.cs`. Without this the
                            // most common patterns (`*.cs`, `*.{ts,tsx}`) match nothing at the
                            // directory that owns the .editorconfig.
                            if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                            {
                                i++;
                                regex.Append("(?:.*/)?");
                            }
                            else
                            {
                                regex.Append(".*");
                            }
                        }
                        else
                        {
                            regex.Append("[^/]*");
                        }
                        break;

                    case '?':
                        regex.Append("[^/]");
                        break;

                    case '[':
                        var close = pattern.IndexOf(']', i + 1);
                        if (close < 0)
                        {
                            regex.Append(Regex.Escape("["));
                            break;
                        }
                        var body = pattern.Substring(i + 1, close - i - 1);
                        if (body.StartsWith("!"))
                            body = "^" + body.Substring(1);
                        regex.Append('[').Append(body).Append(']');
                        i = close;
                        break;

                    case '{':
                        var braceEnd = pattern.IndexOf('}', i + 1);
                        if (braceEnd < 0)
                        {
                            regex.Append(Regex.Escape("{"));
                            break;
                        }
                        regex.Append("(?:").Append(BraceAlternation(pattern.Substring(i + 1, braceEnd - i - 1))).Append(')');
                        i = braceEnd;
                        break;

                    default:
                        regex.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            regex.Append('$');
            return new Regex(regex.ToString(), RegexOptions.IgnoreCase, _matchTimeout);
        }

        /// <summary>`{a,b}` → `a|b`; `{1..9}` → the expanded numbers (bounded, to keep the regex small).</summary>
        private static string BraceAlternation(string body)
        {
            var range = NumericRange(body);
            if (range != null)
                return range;

            return string.Join("|", body.Split(',').Select(part => Regex.Escape(part.Trim())));
        }

        private static string? NumericRange(string body)
        {
            var separator = body.IndexOf("..", StringComparison.Ordinal);
            if (separator < 0)
                return null;
            if (!int.TryParse(body.Substring(0, separator), out var from) || !int.TryParse(body.Substring(separator + 2), out var to))
                return null;

            // A REVERSED range (`{9..1}`) addresses nothing at all, so its alternation must match
            // NOTHING. It used to share the fallback below, which made `[src{9..1}/*.cs]` apply its
            // properties to every numbered file in the tree — the exact opposite of "a header that
            // does not compile matches nothing". `(?!)` is an alternation that can never match.
            if (to < from)
                return "(?!)";

            // A range too wide to expand stays the DOCUMENTED over-approximation: any run of
            // digits. That is a deliberate trade (a small regex instead of 1000 alternatives), not
            // an accident, and it is only reached for a range that is genuinely too large.
            if (to - from > 100)
                return "[0-9]+";

            var values = new List<string>();
            for (var value = from; value <= to; value++)
                values.Add(value.ToString());
            return string.Join("|", values);
        }
    }
}
