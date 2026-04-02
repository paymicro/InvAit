namespace Shared.Contracts;

public static class BasicEnum
{

    // App state
    public const string SwitchMode          = "switch_mode";

    // Skills operations
    public const string GetSkillsMetadata   = "get_skills_metadata";   // Только метаданные (кеш)
    public const string ReadSkillContent    = "read_skill_content";    // Полное содержимое по требованию

    // Read agents.md
    public const string GetAgents           = "get_agents";

    // Rules
    public const string GetRules            = "get_rules";

    // MCP Operations
    public const string McpGetTools         = "mcp_get_tools";
    public const string McpCallTool         = "mcp_call_tool";
    public const string McpStopAll          = "mcp_stop_all";

    // MCP Settings (file-based)
    public const string ReadMcpSettingsFile = "read_mcp_settings_file";
    public const string WriteMcpSettings    = "write_mcp_settings";
    public const string OpenMcpSettings     = "open_mcp_settings";

    // Agent planning (for agent's internal task breakdown)
    public const string Plan                = "plan";
    public const string UpdatePlanProgress  = "update_plan_progress";
    public const string RecordObservation   = "record_observation";
    public const string AdaptPlan           = "adapt_plan";
    public const string FinishPlan          = "finish_plan";

    // User interaction
    public const string AskUser             = "ask_user";

    // System heartbeat
    public const string UIReady             = "ui_ready";
    public const string SkipSSL             = "skip_ssl_validation";
    public const string OpenFile            = "open_file";
    public const string OpenFolder          = "open_folder";
}
