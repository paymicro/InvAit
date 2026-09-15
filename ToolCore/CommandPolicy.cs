namespace ToolCore;

/// <summary>
/// Правило политики команд: разрешение или запрет по маске.
/// </summary>
public sealed class CommandRule(bool allow, string pattern)
{
    public bool Allow { get; } = allow;
    public string Pattern { get; } = pattern ?? throw new ArgumentNullException(nameof(pattern));

    public static CommandRule AllowRule(string pattern) => new(true, pattern);
    public static CommandRule DenyRule(string pattern) => new(false, pattern);
}

/// <summary>
/// Политика разрешений/запретов команд.
/// Правила применяются по порядку, последнее подходящее правило выигрывает.
/// Поддерживается маска '*'. Сопоставление без учёта регистра.
/// </summary>
public sealed class CommandPolicy(IEnumerable<CommandRule>? rules = null)
{
    private readonly List<CommandRule> _rules = rules == null ? [] : [.. rules];

    public bool IsEmpty => _rules.Count == 0;

    /// <summary>
    /// Проверяет, разрешена ли команда.
    /// Если правил нет — разрешено. Если ни одно правило не подошло — разрешено.
    /// Последнее подходящее правило имеет приоритет.
    /// </summary>
    public bool IsAllowed(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
            return true;
        if (_rules.Count == 0)
            return true;

        bool? decision = null;
        foreach (var rule in _rules)
        {
            if (WildcardMatch(rule.Pattern, commandLine))
                decision = rule.Allow;
        }

        return decision ?? true;
    }

    /// <summary>
    /// Простое сопоставление с маской '*' (без учёта регистра).
    /// Классический алгоритм с откатом (backtracking) для одной '*'.
    /// </summary>
    private static bool WildcardMatch(string pattern, string input)
    {
        int p = 0, i = 0;
        int starP = -1, starI = -1;

        while (i < input.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' &&
                char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(input[i]))
            {
                p++;
                i++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starI = i;
            }
            else if (starP != -1)
            {
                p = starP + 1;
                i = ++starI;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}