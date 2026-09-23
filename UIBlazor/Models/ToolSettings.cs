namespace UIBlazor.Models;

public class ToolSettings : BaseOptions
{
    /// <summary>
    /// По умолчанию все категории имеют <see cref="ToolApprovalMode.Allow"/> в <seealso cref="ToolCategorySettings"/>
    /// </summary>
    public Dictionary<ToolCategory, ToolCategorySettings> CategoryStates { get; set; }
        = new() {
            { ToolCategory.Execution, new ToolCategorySettings { ApprovalMode = ToolApprovalMode.Ask } }, // кроме выполнения
            { ToolCategory.DeleteFiles, new ToolCategorySettings { ApprovalMode = ToolApprovalMode.Ask } }, // и удаления
            { ToolCategory.SubAgent, new ToolCategorySettings { ApprovalMode = ToolApprovalMode.Ask } }, // и делегирования sub-agent
        };

    /// <summary>
    /// Список выключенных тулзов
    /// </summary>
    public List<string> DisabledTools { get; set; } = [];

    /// <summary>
    /// Настройки классификатора bash-команд.
    /// Позволяют автоматически определять approval mode на основе содержимого команды.
    /// </summary>
    public BashCommandSettings BashSettings { get; set; } = new();
}

/// <summary>
/// Кастомные паттерны для классификатора bash-команд.
/// Классификатор всегда активен для bash-инструмента.
/// Кастомные паттерны добавляются к встроенным (не заменяют их).
/// </summary>
public class BashCommandSettings
{
    /// <summary>
    /// Кастомные safe-паттерны (read-only команды, которые можно авто-выполнять).
    /// Добавляются к встроенным safe-паттернам.
    /// </summary>
    public List<string> AllowPatterns { get; set; } = [];

    /// <summary>
    /// Пользовательские deny-паттерны — команды, которые всегда авто-отклоняются (Deny без запроса).
    /// Приоритет выше встроенных destructive-паттернов (которые только спрашивают).
    /// </summary>
    public List<string> DenyPatterns { get; set; } = [];
}
