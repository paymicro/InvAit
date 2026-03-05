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
        };

    /// <summary>
    /// Список выключенных тулзов
    /// </summary>
    public List<string> DisabledTools { get; set; } = [];
}
