namespace UIBlazor.Services.Settings;

public interface IToolManager : IBaseSettingsProvider
{
    ToolSettings Current { get; }

    void RegisterAllTools();

    IEnumerable<Tool> GetEnabledTools();

    IEnumerable<Tool> GetAllTools();

    Tool? GetTool(string name);

    ToolApprovalMode GetApprovalModeByToolName(string name);

    string GetToolUseSystemInstructions(AppMode mode, bool hasSkills);

    void UpdateCategorySettings(ToolCategory category, bool isEnabled, ToolApprovalMode approvalMode);

    void ToggleTool(string toolName, bool isEnabled);
}