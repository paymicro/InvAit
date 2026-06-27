namespace Shared.Contracts;

public static class BuiltInToolEnum
{
    // File operations
    public const string ReadFiles = "read_files";
    public const string ReadOpenFile = "read_open_file";
    public const string CreateFile = "create_file";
    public const string DeleteFile = "delete_file";
    public const string ApplyDiff = "apply_diff";

    // Search and navigation
    public const string SearchFiles = "search_files";
    public const string Grep = "grep";
    public const string FindDeclarations = "find_declarations";
    public const string FindReferences = "find_references";
    public const string Dir = "dir";

    // Project
    public const string GetErrors = "get_error_list";
    public const string GetProjectInfo = "get_project_info";
    public const string GetSolutionStructure = "get_solution_structure";
    public const string RunTests = "run_tests";
    public const string Build = "build";

    // Execution
    public const string Bash = "bash";

    // Git operations
    public const string GitLog = "git_log";
    public const string GitDiff = "git_diff";
    public const string GitStatus = "git_status";
}
