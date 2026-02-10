using UIBlazor.Components.Chat;

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

    List<(string ToolName, string CallId, string Args)> ParseToolBlockRaw(string content);

    List<AiTool> ParseToolBlock(string content);

    IEnumerable<AiTool> ParseToolBlock(List<ContentSegment> segments);

    Dictionary<string, object> Parse(string toolName, List<string> toolLines);

    void UpdateCategorySettings(ToolCategory category, bool isEnabled, ToolApprovalMode approvalMode);

    void ToggleTool(string toolName, bool isEnabled);
}