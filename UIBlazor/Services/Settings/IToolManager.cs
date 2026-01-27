namespace UIBlazor.Services.Settings;

public interface IToolManager : IBaseSettingsProvider, IDisposable
{
    ToolSettings Current { get; }
    
    void RegisterAllTools();

    Task SaveToolSettingsAsync();

    IEnumerable<Tool> GetEnabledTools();

    IEnumerable<Tool> GetAllTools();

    Tool? GetTool(string name);

    string GetToolUseSystemInstructions(AppMode mode);

    List<AiTool> ParseToolBlock(string content);

    void UpdateCategorySettings(ToolCategory category, bool isEnabled, ToolApprovalMode approvalMode);
    
    void ToggleTool(string toolName, bool isEnabled);
}