namespace Shared.Contracts;

public static class BuiltInToolEnum
{
    public const string ReadFiles       = "read_files";
    public const string ReadOpenFile    = "read_currently_open_file";
    public const string CreateFile      = "create_new_file";
    public const string Exec            = "execute_command";
    public const string SearchFiles     = "search_files";
    public const string GrepSearch      = "grep_search";
    public const string Ls              = "ls";
    public const string FetchUrl        = "fetch_url_content";
    public const string ApplyDiff       = "apply_diff";
    public const string Build           = "build_solution";
    public const string GetErrors       = "get_error_list";
}
