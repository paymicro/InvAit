using UIBlazor.Agents;
using UIBlazor.Models;

namespace UIBlazor.Services.Settings;

public interface IToolManager : IBaseSettingsProvider, IDisposable
{
    ToolSettings Current { get; }
    void RegisterAllTools();

    Task SaveToolSettingsAsync();

    IEnumerable<Tool> GetEnabledTools();

    IEnumerable<Tool> GetAllTools();

    Tool? GetTool(string name);

    string GetToolUseSystemInstructions(Shared.Contracts.AppMode mode);

    List<AiTool> ParseToolBlock(string content);

    void UpdateCategorySettings(Shared.Contracts.ToolCategory category, bool isEnabled, Shared.Contracts.ToolApprovalMode approvalMode);
    void ToggleTool(string toolName, bool isEnabled);
}