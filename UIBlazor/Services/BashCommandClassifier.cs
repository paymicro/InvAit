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

        if (settings is { AllowPatterns.Count: > 0 })
            safePatterns = [.. DefaultSafePatterns, .. settings.AllowPatterns];

        _safeRegexes = CompilePatterns(safePatterns);
        _destructiveRegexes = CompilePatterns(DefaultDestructivePatterns);

        _userDeniedRegexes = settings is { DenyPatterns.Count: > 0 }
            ? CompilePatterns(settings.DenyPatterns)
            : [];
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
