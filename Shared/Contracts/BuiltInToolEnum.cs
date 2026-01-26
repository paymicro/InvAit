namespace Shared.Contracts;

public static class BuiltInToolEnum
{
    // File operations
    public const string ReadFiles       = "read_files";
    public const string ReadOpenFile    = "read_currently_open_file";
    public const string CreateFile      = "create_new_file";
    public const string ApplyDiff       = "apply_diff";
    
    // Search and navigation
    public const string SearchFiles     = "search_files";
    public const string GrepSearch      = "grep_search";
    public const string Ls              = "ls";
    
    // Project and build
    public const string Build           = "build_solution";
    public const string GetErrors       = "get_error_list";
    public const string GetProjectInfo  = "get_project_info";
    public const string GetSolutionStructure = "get_solution_structure";
    
    // Execution
    public const string Exec            = "execute_command";
    public const string FetchUrl        = "fetch_url_content";
    
    // Git operations
    public const string GitLog          = "git_log";
    public const string GitDiff         = "git_diff";
    public const string GitStatus       = "git_status";
    public const string GitBranch       = "git_branch";

    // App state
    public const string SwitchMode      = "switch_mode";
    
    // Skills operations
    public const string GetSkillsMetadata   = "get_skills_metadata";   // Только метаданные (кеш)
    public const string ReadSkillContent    = "read_skill_content";    // Полное содержимое по требованию
    
    // MCP Process Management
    public const string McpStartProcess     = "mcp_start_process";
    public const string McpStopProcess      = "mcp_stop_process";
    public const string McpSendMessage      = "mcp_send_message";
    public const string McpReadMessage      = "mcp_read_message";
    public const string McpGetTools         = "mcp_get_tools";

    // Agent planning (for agent's internal task breakdown)
    public const string Plan                = "plan";
    public const string UpdatePlanProgress  = "update_plan_progress";
    public const string RecordObservation   = "record_observation";
    public const string AdaptPlan           = "adapt_plan";
    public const string FinishPlan          = "finish_plan";
}
