namespace UIBlazor.Services.Settings;

public interface IToolManager : IBaseSettingsProvider, IDisposable
{
    ToolSettings Current { get; }
    
    void RegisterAllTools();

    Task SaveToolSettingsAsync();

    IEnumerable<Tool> GetEnabledTools();

    IEnumerable<Tool> GetAllTools();

    Tool? GetTool(string name);

    ToolApprovalMode GetApprovalModeByToolName(string name);

    string GetToolUseSystemInstructions(AppMode mode);

    Dictionary<string, object> Parse(string toolName, List<string> toolLines);

    void UpdateCategorySettings(ToolCategory category, bool isEnabled, ToolApprovalMode approvalMode);

    void ToggleTool(string toolName, bool isEnabled);
}