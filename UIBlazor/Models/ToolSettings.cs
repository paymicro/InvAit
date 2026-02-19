namespace UIBlazor.Models;

public class ToolSettings : BaseOptions
{
    /// <summary>
    /// По умолчанию все категории имеют <see cref="ToolApprovalMode.AutoApprove"/> в <seealso cref="ToolCategorySettings"/>
    /// </summary>
    public Dictionary<ToolCategory, ToolCategorySettings> CategoryStates { get; set; }
        = new() { { ToolCategory.Execution, new ToolCategorySettings { ApprovalMode = ToolApprovalMode.AlwaysAsk } } }; // кроме выполнения

    /// <summary>
    /// Список выключенных тулзов
    /// </summary>
    public List<string> DisabledTools { get; set; } = [];
}
