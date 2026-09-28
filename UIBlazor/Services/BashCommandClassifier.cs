namespace UIBlazor.Services;

/// <summary>
/// Классификация bash-команды по уровню опасности.
/// </summary>
public enum BashCommandClassification
{
    /// <summary>
    /// Безопасная read-only команда (git status, ls, cat, ...).
    /// </summary>
    Safe,

    /// <summary>
    /// Команда не распознана паттернами — требуется решение пользователя.
    /// </summary>
    Unknown,

    /// <summary>
    /// Деструктивная команда из встроенных паттернов (rm -rf /, format, shutdown, ...).
    /// Требует подтверждения (Ask), но не авто-отклоняется.
    /// </summary>
    Destructive,

    /// <summary>
    /// Команда из пользовательских deny-паттернов.
    /// Авто-отклоняется (Deny) без запроса.
    /// </summary>
    UserDenied,
}

/// <summary>
/// Результат детальной классификации bash-команды.
/// Содержит не только уровень опасности, но и информацию о том,
/// какой паттерн сработал и откуда он (встроенный или пользовательский).
/// </summary>
/// <param name="Classification">Уровень опасности команды.</param>
/// <param name="MatchedPattern">Паттерн, который сработал (null, если ничего не match).</param>
/// <param name="MatchSource">Источник срабатывания: "Built-in safe", "Custom safe",
/// "Built-in destructive", "Custom deny", "Substitution detected", "No match".</param>
public record BashClassificationResult(
    BashCommandClassification Classification,
    string? MatchedPattern,
    string MatchSource);

/// <summary>
/// Внутренняя запись для отслеживания источника паттерна.
/// </summary>
internal record PatternInfo(Regex Regex, string Pattern, bool IsCustom);

/// <summary>
/// Классифицирует bash-команды по содержимому.
/// <para>
/// Для цепочек команд (разделённых &amp;&amp;, ||, ;, |, \n) берётся
/// <b>максимальный</b> уровень опасности среди всех частей:
/// <c>Safe &lt; Unknown &lt; Destructive &lt; UserDenied</c>.
/// </para>
/// <para>
/// Классификатор всегда активен для bash-инструмента.
/// Окончательный <see cref="ToolApprovalMode"/> определяется
/// <see cref="ToolCallHandler"/> на основе классификации и category mode.
/// </para>
/// </summary>
public class BashCommandClassifier
{
    /// <summary>
    /// Встроенные safe-паттерны — безопасные read-only команды.
    /// </summary>
    public static readonly string[] DefaultSafePatterns =
    [
        // Git read-only commands
        @"^git (status|log|diff|branch|show|stash list|remote -v|rev-parse|blame)(\s|$)",
        @"^git (config --list|describe|shortlog|reflog)(\s|$)",
        // Common read-only commands
        @"^(ls|dir|cat|echo|pwd|whoami|type|where|which|head|tail|wc)(\s|$)",
        // Version checks
        @"^(dotnet --version|dotnet --info)(\s|$)",
        @"^(node --version|npm --version|python --version)(\s|$)",
    ];

    /// <summary>
    /// Встроенные destructive-паттерны — деструктивные команды.
    /// Эти команды требуют подтверждения (Ask), но не авто-отклоняются.
    /// Все паттерны анкорены (^), чтобы избежать ложных срабатываний.
    /// </summary>
    public static readonly string[] DefaultDestructivePatterns =
    [
        // rm with recursive+force flags in any order/combination targeting root or wildcards
        @"^rm\s+(-\S*r\S*f\S*|-\S*f\S*r\S*|-r\s+-f|-f\s+-r)\s+.*(/|\*|~)",
        // format with optional switches before drive letter
        @"^format\s+(/\S+\s+)*[A-Z]:",
        @"^(shutdown|reboot|halt)(\s|$)",
        @"^del\s+/[sf]\s+[A-Z]:\\",
        @"^:\s*\(\s*\)\s*\{", // fork bomb (:(){ ... })
        @"^mkfs\.",
        @"^dd\s+[^|;&\n]*of=/dev/",
    ];

    private readonly Regex[] _safeRegexes;
    private readonly Regex[] _destructiveRegexes;
    private readonly Regex[] _userDeniedRegexes;

    // Parallel lists for detailed classification — track original pattern strings and source
    private readonly PatternInfo[] _safeInfos;
    private readonly PatternInfo[] _destructiveInfos;
    private readonly PatternInfo[] _deniedInfos;

    /// <summary>
    /// Detects command/process substitution: <c>$(cmd)</c> (but not arithmetic <c>$((expr))</c>),
    /// <c>&lt;(cmd)</c>, and backticks. Presence downgrades Safe → Unknown.
    /// </summary>
    private static readonly Regex _substitutionRegex = new(@"\$\((?!\()|<\(|`", RegexOptions.Compiled);

    /// <summary>
    /// Hash of settings used to create this classifier instance.
    /// Used by <see cref="ToolCallHandler"/> to detect when recreation is needed.
    /// </summary>
    internal string? SettingsHash { get; set; }

    /// <summary>
    /// Создаёт классификатор со встроенными паттернами и опциональными кастомными.
    /// Пустые и невалидные паттерны пропускаются.
    /// </summary>
    /// <param name="settings">Кастомные настройки (null — только встроенные паттерны).</param>
    public BashCommandClassifier(BashCommandSettings? settings = null)
    {
        var safePatterns = DefaultSafePatterns;
        var customSafePatterns = Array.Empty<string>();

        if (settings is { AllowPatterns.Count: > 0 })
        {
            customSafePatterns = [.. settings.AllowPatterns];
            safePatterns = [.. DefaultSafePatterns, .. settings.AllowPatterns];
        }

        _safeRegexes = CompilePatterns(safePatterns);
        _destructiveRegexes = CompilePatterns(DefaultDestructivePatterns);

        var denyPatterns = settings is { DenyPatterns.Count: > 0 }
            ? settings.DenyPatterns
            : [];

        _userDeniedRegexes = CompilePatterns(denyPatterns);

        // Build pattern info arrays for detailed classification
        _safeInfos = [.. BuildPatternInfos(DefaultSafePatterns, false), .. BuildPatternInfos(customSafePatterns, true)];
        _destructiveInfos = BuildPatternInfos(DefaultDestructivePatterns, false);
        _deniedInfos = BuildPatternInfos(denyPatterns, true);
    }

    /// <summary>
    /// Компилирует паттерны в массив PatternInfo, пропуская пустые и невалидные.
    /// </summary>
    private static PatternInfo[] BuildPatternInfos(IEnumerable<string> patterns, bool isCustom)
    {
        var infos = new List<PatternInfo>();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            try
            {
                infos.Add(new PatternInfo(
                    new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled),
                    pattern,
                    isCustom));
            }
            catch (ArgumentException)
            {
                // Skip invalid regex pattern
            }
        }
        return [.. infos];
    }

    /// <summary>
    /// Компилирует паттерны в Regex, пропуская пустые и невалидные.
    /// Пустой regex match'ит всё — поэтому пустые паттерны критичны для безопасности.
    /// </summary>
    private static Regex[] CompilePatterns(IEnumerable<string> patterns)
    {
        var regexes = new List<Regex>();
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                continue;
            try
            {
                regexes.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
            }
            catch (ArgumentException)
            {
                // Skip invalid regex pattern
            }
        }
        return [.. regexes];
    }

    /// <summary>
    /// Классифицирует bash-команду.
    /// <para>
    /// 1. Split по &amp;&amp;, ||, ;, |, \n — классифицировать каждую часть.<br/>
    /// 2. Взять максимальный уровень опасности среди всех частей.<br/>
    /// 3. Если ни один pattern не match — вернуть <see cref="BashCommandClassification.Unknown"/>.
    /// </para>
    /// </summary>
    public BashCommandClassification Classify(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return BashCommandClassification.Unknown;

        var parts = SplitCommandChain(command);

        if (parts.Length == 0)
            return BashCommandClassification.Unknown;

        var maxLevel = BashCommandClassification.Safe; // Start with lowest
        var anyClassified = false;

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            var partLevel = ClassifySingle(trimmed);
            anyClassified = true;

            // Take maximum danger level: Safe (0) < Unknown (1) < Destructive (2) < UserDenied (3)
            if ((int)partLevel > (int)maxLevel)
                maxLevel = partLevel;
        }

        // If no part was actually classified (e.g. all whitespace between separators),
        // return Unknown rather than the default Safe.
        return anyClassified ? maxLevel : BashCommandClassification.Unknown;
    }

    /// <summary>
    /// Классифицирует одну команду (без разделителей цепочки).
    /// </summary>
    private BashCommandClassification ClassifySingle(string command)
    {
        // Check user-specified deny patterns first — auto-reject
        if (_userDeniedRegexes.Length > 0 && _userDeniedRegexes.Any(r => r.IsMatch(command)))
            return BashCommandClassification.UserDenied;

        // Check built-in destructive patterns — ask
        if (_destructiveRegexes.Any(r => r.IsMatch(command)))
            return BashCommandClassification.Destructive;

        // Check safe
        if (_safeRegexes.Any(r => r.IsMatch(command)))
        {
            // Command/process substitution can hide destructive commands inside
            // safe-looking commands, e.g. echo $(rm -rf /) or cat `rm -rf /`.
            // $((expr)) arithmetic expansion is excluded (safe, just math).
            // Downgrade to Unknown so the user is asked in Ask mode.
            if (_substitutionRegex.IsMatch(command))
                return BashCommandClassification.Unknown;
            return BashCommandClassification.Safe;
        }

        // Default fallback
        return BashCommandClassification.Unknown;
    }

    /// <summary>
    /// Детально классифицирует одну команду (без разделителей цепочки).
    /// Возвращает не только уровень опасности, но и какой паттерн сработал и откуда.
    /// </summary>
    private BashClassificationResult ClassifySingleDetailed(string command)
    {
        // Check user-specified deny patterns first — auto-reject
        foreach (var info in _deniedInfos)
        {
            if (info.Regex.IsMatch(command))
                return new BashClassificationResult(
                    BashCommandClassification.UserDenied,
                    info.Pattern,
                    "Custom deny");
        }

        // Check built-in destructive patterns — ask
        foreach (var info in _destructiveInfos)
        {
            if (info.Regex.IsMatch(command))
                return new BashClassificationResult(
                    BashCommandClassification.Destructive,
                    info.Pattern,
                    "Built-in destructive");
        }

        // Check safe
        foreach (var info in _safeInfos)
        {
            if (info.Regex.IsMatch(command))
            {
                // Command/process substitution can hide destructive commands inside
                // safe-looking commands, e.g. echo $(rm -rf /) or cat `rm -rf /`.
                // $((expr)) arithmetic expansion is excluded (safe, just math).
                // Downgrade to Unknown so the user is asked in Ask mode.
                if (_substitutionRegex.IsMatch(command))
                    return new BashClassificationResult(
                        BashCommandClassification.Unknown,
                        info.Pattern,
                        "Substitution detected");
                return new BashClassificationResult(
                    BashCommandClassification.Safe,
                    info.Pattern,
                    info.IsCustom ? "Custom safe" : "Built-in safe");
            }
        }

        // Default fallback
        return new BashClassificationResult(
            BashCommandClassification.Unknown,
            null,
            "No match");
    }

    /// <summary>
    /// Детально классифицирует bash-команду.
    /// <para>
    /// Аналог <see cref="Classify"/>, но дополнительно возвращает информацию о том,
    /// какой паттерн сработал и откуда он (встроенный или пользовательский).
    /// </para>
    /// <para>
    /// Для цепочек команд возвращает результат для наиболее опасной части.
    /// </para>
    /// </summary>
    public BashClassificationResult ClassifyDetailed(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new BashClassificationResult(
                BashCommandClassification.Unknown,
                null,
                "No match");

        var parts = SplitCommandChain(command);

        if (parts.Length == 0)
            return new BashClassificationResult(
                BashCommandClassification.Unknown,
                null,
                "No match");

        BashClassificationResult? maxResult = null;
        var anyClassified = false;

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            var partResult = ClassifySingleDetailed(trimmed);
            anyClassified = true;

            // Take maximum danger level: Safe (0) < Unknown (1) < Destructive (2) < UserDenied (3)
            if (maxResult is null || (int)partResult.Classification > (int)maxResult.Classification)
                maxResult = partResult;
        }

        return anyClassified
            ? maxResult!
            : new BashClassificationResult(
                BashCommandClassification.Unknown,
                null,
                "No match");
    }

    /// <summary>
    /// Детально классифицирует каждую часть цепочки команд по отдельности.
    /// Возвращает список (текст части, результат классификации) для каждой непустой части.
    /// </summary>
    public List<(string Part, BashClassificationResult Result)> ClassifyDetailedParts(string? command)
    {
        var results = new List<(string Part, BashClassificationResult Result)>();

        if (string.IsNullOrWhiteSpace(command))
            return results;

        var parts = SplitCommandChain(command);

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            results.Add((trimmed, ClassifySingleDetailed(trimmed)));
        }

        return results;
    }

    /// <summary>
    /// Разбивает цепочку команд по разделителям: &amp;&amp;, ||, ;, |, переносы строк (\n, \r\n).
    /// Переносы строк учитываются, т.к. bash-команда пишется в файл и может быть многострочным скриптом.
    /// <para>
    /// Ограничение: одинарный &amp; (background operator) не разделяется.
    /// Кавычки не обрабатываются — разделители внутри кавычек всё равно разбивают строку.
    /// Это допустимо для эвристического классификатора.
    /// </para>
    /// </summary>
    private static string[] SplitCommandChain(string command)
    {
        // Normalize line endings first, then replace multi-char separators
        // with single-char sentinels to avoid splitting on single & or |
        var normalized = command
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("&&", "\x01")
            .Replace("||", "\x02");

        // Split by \x01 (&&), \x02 (||), ;, |, \n (newline)
        return normalized.Split(['\x01', '\x02', ';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }
}
