namespace UIBlazor.Services.Settings;

public interface IToolManager : IBaseSettingsProvider
{
    ToolSettings Current { get; }

    void RegisterAllTools();

    IEnumerable<Tool> GetEnabledTools(AppMode mode);

    IEnumerable<Tool> GetAllTools();

    IEnumerable<Tool> GetBuiltInTools();

    IEnumerable<Tool> GetMcpTools();

    Tool? GetTool(string name);

    ToolApprovalMode GetApprovalModeByToolName(string name);

    void UpdateCategorySettings(ToolCategory category, bool isEnabled, ToolApprovalMode approvalMode);

    void ToggleTool(string toolName, bool isEnabled);
}
