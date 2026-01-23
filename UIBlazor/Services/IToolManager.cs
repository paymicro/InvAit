using UIBlazor.Agents;
using UIBlazor.Models;

namespace UIBlazor.Services;

public interface IToolManager
{
    void RegisterAllTools();

    Task LoadToolSettingsAsync();

    Task SaveToolSettingsAsync();

    IEnumerable<Tool> GetEnabledTools();

    IEnumerable<Tool> GetAllTools();

    Tool? GetTool(string name);

    string GetToolUseSystemInstructions(string promptFromOptions);

    List<AiTool> ParseToolBlock(string content);
}