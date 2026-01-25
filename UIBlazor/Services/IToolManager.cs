using UIBlazor.Agents;
using UIBlazor.Models;

namespace UIBlazor.Services;

public interface IToolManager : IDisposable
{
    void RegisterAllTools();

    Task LoadToolSettingsAsync();

    Task SaveToolSettingsAsync();

    IEnumerable<Tool> GetEnabledTools();

    IEnumerable<Tool> GetAllTools();

    Tool? GetTool(string name);

    string GetToolUseSystemInstructions(string promptFromOptions, Shared.Contracts.AppMode mode);

    List<AiTool> ParseToolBlock(string content);
}