using System.Text.RegularExpressions;
using Shared.Contracts;
using UIBlazor.Services.Settings;

namespace UIBlazor.Services;

/// <summary>
/// Applies content filtering transformations (strip comments, blank lines,
/// trim whitespace, custom regex rules) to tool result content before sending to LLM.
/// Reads configuration from <see cref="ICommonSettingsProvider"/>.
/// </summary>
public class ContentFilterService(ICommonSettingsProvider commonSettingsProvider) : IContentFilter
{
    private static readonly Regex BlankLineRegex = new(
        @"^\s*$\n", RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex TrailingWhitespaceRegex = new(
        @"[ \t]+$", RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex MultiBlankLineRegex = new(
        @"\n{3,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private Dictionary<string, Regex>? _customRegexCache;
    private int _cachedRuleHash;

    private ICommonSettingsProvider CommonSettingsProvider { get; } = commonSettingsProvider;

    /// <inheritdoc/>
    public bool IsActive
    {
        get
        {
            var s = CommonSettingsProvider.Current.ContentFilter;
            if (!s.IsEnabled)
                return false;
            return s.StripComments || s.StripBlankLines || s.TrimWhitespace ||
                   s.CustomRules.Any(r => r.IsEnabled && !string.IsNullOrEmpty(r.Pattern));
        }
    }

    /// <inheritdoc/>
    public string Filter(string content, string toolName)
    {
        var settings = CommonSettingsProvider.Current.ContentFilter;
        if (string.IsNullOrEmpty(content))
            return content;

        // Even when filtering is disabled, structured FileContent JSON must be
        // formatted into human-readable text with line numbers for the LLM.
        if (!settings.IsEnabled)
            return FileContentFormatter.FormatContent(content);

        // Try structured FileContent[] first
        var files = FileContentFormatter.TryDeserialize(content);
        if (files is not null)
            return FileContentFormatter.Format(FilterFileContents(files, settings));

        // Fallback: raw text filtering (for non-file tools, old sessions, etc.)
        return FilterRawText(content, settings);
    }

    /// <summary>
    /// Filters structured <see cref="FileContent"/> arrays — operates on clean lines,
    /// no need to parse markdown fences.
    /// </summary>
    public List<FileContent> FilterFileContents(List<FileContent> files, ContentFilterSettings? settings = null)
    {
        settings ??= CommonSettingsProvider.Current.ContentFilter;
        if (!settings.IsEnabled)
            return files;

        var result = new List<FileContent>(files.Count);
        foreach (var fc in files)
        {
            if (!string.IsNullOrEmpty(fc.Error) || fc.Lines is null || fc.Lines.Count == 0)
            {
                result.Add(fc);
                continue;
            }

            var filteredLines = fc.Lines;

            if (settings.StripComments)
            {
                filteredLines = FilterCommentLines(filteredLines, fc.Path);
            }

            if (settings.TrimWhitespace)
            {
                filteredLines = filteredLines.Select(l => l.TrimEnd()).ToList();
            }

            if (settings.StripBlankLines)
            {
                filteredLines = filteredLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            }

            // Apply custom rules per file
            var cache = GetCustomRegexCache(settings);
            if (cache.Count > 0)
            {
                var filePath = fc.Path ?? "";
                foreach (var rule in settings.CustomRules)
                {
                    if (!rule.IsEnabled || string.IsNullOrEmpty(rule.Pattern))
                        continue;
                    if (!string.IsNullOrEmpty(rule.FilePattern) && !MatchesFilePattern(filePath, rule.FilePattern))
                        continue;
                    if (cache.TryGetValue(rule.Pattern, out var regex))
                    {
                        filteredLines = filteredLines
                            .Select(l => SafeReplace(regex, l, rule.Replacement))
                            .ToList();
                    }
                }
            }

            result.Add(new FileContent
            {
                Path = fc.Path,
                Lines = filteredLines,
                TotalLines = fc.TotalLines,
                StartLine = fc.StartLine,
                Error = fc.Error
            });
        }

        return result;
    }

    /// <summary>
    /// File extensions where # is a comment character (Python, Shell, Ruby, etc.).
    /// C/C++ files (.c, .h, .cpp, .hpp, .cs) use # for preprocessor directives, NOT comments.
    /// </summary>
    private static readonly HashSet<string> HashCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".sh", ".bash", ".zsh", ".rb", ".pl", ".yaml", ".yml", ".toml", ".ini", ".conf", ".cfg", ".r", ".ps1"
    };

    /// <summary>
    /// File extensions where -- is a comment character (SQL, Lua, Haskell).
    /// </summary>
    private static readonly HashSet<string> DashCommentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sql", ".lua", ".hs"
    };

    /// <summary>
    /// Removes comment lines from a list of source code lines.
    /// Handles: // (C-family), # (Python/Ruby/Shell, but not #!), -- (SQL/Lua), /* */ (block).
    /// Language detection is based on file extension — see <see cref="HashCommentExtensions"/> and <see cref="DashCommentExtensions"/>.
    /// Note: string literals containing /* or // are not handled (would require a full lexer).
    /// </summary>
    private static List<string> FilterCommentLines(List<string> lines, string? filePath = null)
    {
        var isHashCommentLang = IsHashCommentFile(filePath);
        var isDashCommentLang = IsDashCommentFile(filePath);

        var result = new List<string>(lines.Count);
        var inBlockComment = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            // Inside a block comment — skip until we find */
            if (inBlockComment)
            {
                var endIdx = trimmed.IndexOf("*/", StringComparison.Ordinal);
                if (endIdx >= 0)
                {
                    inBlockComment = false;
                    // Check if there's code after */
                    var after = trimmed.Substring(endIdx + 2).Trim();
                    if (!string.IsNullOrEmpty(after))
                        result.Add(after);
                }
                continue;
            }

            // Single-line // comment — check before /* to avoid false positives like `// /* not real */`
            if (trimmed.StartsWith("//"))
                continue;

            // Block comment start
            var blockStart = trimmed.IndexOf("/*", StringComparison.Ordinal);
            if (blockStart >= 0)
            {
                var blockEnd = trimmed.IndexOf("*/", blockStart + 2, StringComparison.Ordinal);
                if (blockEnd >= 0)
                {
                    // Single-line block comment — keep code before and after
                    var before = trimmed.Substring(0, blockStart).Trim();
                    var after = trimmed.Substring(blockEnd + 2).Trim();
                    if (!string.IsNullOrEmpty(before))
                        result.Add(before);
                    if (!string.IsNullOrEmpty(after))
                        result.Add(after);
                }
                else
                {
                    // Multi-line block comment starts
                    inBlockComment = true;
                    var before = trimmed.Substring(0, blockStart).Trim();
                    if (!string.IsNullOrEmpty(before))
                        result.Add(before);
                }
                continue;
            }

            // SQL/Lua/Haskell comment
            if (isDashCommentLang && trimmed.StartsWith("--"))
                continue;
            // Hash comment but not shebang (#!) — only for script languages, not C/C++
            if (isHashCommentLang && trimmed.StartsWith("#") && !trimmed.StartsWith("#!"))
                continue;

            result.Add(line);
        }

        return result;
    }

    /// <summary>
    /// Determines if # is a comment character for the given file path's language.
    /// Defaults to false (safe: don't strip # for unknown languages).
    /// </summary>
    private static bool IsHashCommentFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;
        var ext = System.IO.Path.GetExtension(filePath);
        return HashCommentExtensions.Contains(ext);
    }

    /// <summary>
    /// Determines if -- is a comment character for the given file path's language.
    /// </summary>
    private static bool IsDashCommentFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;
        var ext = System.IO.Path.GetExtension(filePath);
        return DashCommentExtensions.Contains(ext);
    }

    private static bool MatchesFilePattern(string filePath, string pattern)
    {
        // Simple glob: *.cs, *.py, etc.
        if (string.IsNullOrEmpty(pattern) || pattern == "*.*")
            return true;
        if (pattern.StartsWith("*."))
        {
            var ext = pattern.Substring(1); // ".cs"
            return filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }
        return filePath.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fallback for non-file tools (bash output, etc.) and old sessions.
    /// Comment stripping is NOT applied here — it requires structured FileContent
    /// with known file extension to distinguish # comments from # preprocessor directives.
    /// </summary>
    private string FilterRawText(string content, ContentFilterSettings settings)
    {
        var result = content;

        if (settings.TrimWhitespace)
        {
            result = SafeReplace(TrailingWhitespaceRegex, result, "");
            result = SafeReplace(MultiBlankLineRegex, result, "\n\n");
        }

        if (settings.StripBlankLines)
        {
            result = SafeReplace(BlankLineRegex, result, "");
        }

        // Apply custom rules
        var cache = GetCustomRegexCache(settings);
        foreach (var rule in settings.CustomRules)
        {
            if (!rule.IsEnabled || string.IsNullOrEmpty(rule.Pattern))
                continue;
            if (cache.TryGetValue(rule.Pattern, out var regex))
                result = SafeReplace(regex, result, rule.Replacement);
        }

        return result;
    }

    private static string SafeReplace(Regex regex, string input, string replacement)
    {
        try
        {
            return regex.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    private Dictionary<string, Regex> GetCustomRegexCache(ContentFilterSettings settings)
    {
        var hash = settings.CustomRules.Count;
        foreach (var r in settings.CustomRules)
            hash = HashCode.Combine(hash, r.Pattern, r.IsEnabled);

        if (_customRegexCache is not null && hash == _cachedRuleHash)
            return _customRegexCache;

        _cachedRuleHash = hash;
        _customRegexCache = [];

        foreach (var rule in settings.CustomRules)
        {
            if (string.IsNullOrEmpty(rule.Pattern))
                continue;
            if (_customRegexCache.ContainsKey(rule.Pattern))
                continue;
            try
            {
                _customRegexCache[rule.Pattern] = new Regex(
                    rule.Pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
            }
            catch (ArgumentException)
            {
                // Invalid regex pattern — skip
            }
        }

        return _customRegexCache;
    }
}
