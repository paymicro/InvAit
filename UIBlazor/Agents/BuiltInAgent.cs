using Shared.Contracts;
using UIBlazor.VS;

namespace UIBlazor.Agents;

public class BuiltInAgent(IVsBridge vsBridge)
{
    public IReadOnlyList<Tool> Tools =
    [
        // File operations
        new()
        {
            Name = BuiltInToolEnum.ReadFiles,
            Category = ToolCategory.ReadFiles,
            Description = "Request to read the contents of one or more files. Use start_line and line_count to read specific parts of large files.",
            ExampleToSystemMessage = """
                                     For example, to read a specific range:
                                     <tool_call_begin> functions.read_files
                                     path/to/large_file.cs
                                     start_line
                                     100
                                     line_count
                                     50
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, args)
        },
        new()
        {
            Name = BuiltInToolEnum.ReadOpenFile,
            Category = ToolCategory.ReadFiles,
            Description = """
                          To view the user's currently open file, use the read_currently_open_file tool. The tool outputs line-numbered content (e.g. "1 | const x = 1")
                          If the user is asking about a file and you don't see any code, use this to check the current file
                          """,
            ExampleToSystemMessage = """
                                     For example
                                     <tool_call_begin> functions.read_currently_open_file <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadOpenFile, args)
        },
        new()
        {
            Name = BuiltInToolEnum.CreateFile,
            Category = ToolCategory.WriteFiles,
            Description = "To create a NEW file, use the create_new_file tool with the relative or absolute filepath and new contents.",
            ExampleToSystemMessage = """
                                     For example, to create a file located at 'path\to\file.cs', you would respond with:
                                     <tool_call_begin> functions.create_new_file
                                     \path\to\file.cs
                                     Contents of the file.
                                     And second line of this file.
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.CreateFile, args)
        },
        new()
        {
            Name = BuiltInToolEnum.ApplyDiff,
            Category = ToolCategory.WriteFiles,
            Description = """
                          Request to apply PRECISE, TARGETED modifications to an existing file by searching for specific sections of content and replacing them. This tool is for SURGICAL EDITS ONLY - specific changes to existing code.
                          You can perform multiple distinct search and replace operations within a single `apply_diff` call by providing multiple SEARCH/REPLACE blocks in the `diff` parameter. This is the preferred way to make several targeted changes efficiently.
                          The SEARCH section must exactly match existing content including whitespace and indentation.
                          If you're not confident in the exact content to search for, use the read_file tool first to get the exact content.
                          When applying the diffs, be extra careful to remember to change any closing brackets or other syntax that may be affected by the diff farther down in the file.
                          ALWAYS make as many changes in a single 'apply_diff' request as possible using multiple SEARCH/REPLACE blocks.
                          An optional ":start_line:". The search will be 5 lines up and down.
                          """,
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.apply_diff
                                     C:\path\to\file.cs
                                     :start_line:10
                                     <<<<<<< SEARCH
                                     old code
                                     =======
                                     new code
                                     >>>>>>> REPLACE
                                     <tool_call_end>
                                     
                                     <tool_call_begin> functions.apply_diff
                                     C:\path\to\file222.cs
                                     <<<<<<< SEARCH
                                     old code
                                     =======
                                     new code
                                     >>>>>>> REPLACE
                                     
                                     :start_line:40
                                     <<<<<<< SEARCH
                                         var z = "old code";
                                     =======
                                         var isNew = true;
                                         var z = isNew ? "new code" : "old code";
                                     >>>>>>> REPLACE
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ApplyDiff, args)
        },
        
        // Search and navigation
        new()
        {
            Name = BuiltInToolEnum.SearchFiles,
            Category = ToolCategory.ReadFiles,
            Description = "To return a list of files with patches in solution directory based on a search regex pattern, use the search_files tool.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.search_files
                                     ^.*\.cs$
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.SearchFiles, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GrepSearch,
            Category = ToolCategory.ReadFiles,
            Description = "To perform a grep search within the project, call the grep_search tool with the regex pattern to match.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.grep_search
                                     ^.*?main_services.*
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GrepSearch, args)
        },
        new()
        {
            Name = BuiltInToolEnum.Ls,
            Category = ToolCategory.ReadFiles,
            Description = "To list files and folders in a given directory, call the ls tool with \"dirPath\" and \"recursive\".",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.ls
                                     C:\path\to\dir
                                     false
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Ls, args)
        },
        
        // Project and build
        new()
        {
            Name = BuiltInToolEnum.Build,
            Category = ToolCategory.Execution,
            Description = "To build solution in Visual Studio. With action - Build, Rebuild or Clean. When any errors returns errors list.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.build_solution
                                     build
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Build, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GetErrors,
            Category = ToolCategory.ReadFiles,
            Description = "To get error list of current solution and current file from Visual Studio.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.get_error_list <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetErrors)
        },
        new()
        {
            Name = BuiltInToolEnum.GetProjectInfo,
            Category = ToolCategory.ReadFiles,
            Description = "Get information about the solution and projects. Returns list of projects, their types, target frameworks, and file structure.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.get_project_info <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetProjectInfo, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GetSolutionStructure,
            Category = ToolCategory.ReadFiles,
            Description = "Get a tree-like structure of the entire solution, including projects, folders, and files.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.get_solution_structure <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure)
        },
        
        // Execution
        new()
        {
            Name = BuiltInToolEnum.Exec,
            Category = ToolCategory.Execution,
            Description = """
                          To run a terminal command, use the execute_command tool in
                          The shell is not stateful and will not remember any previous commands.
                          When a command is run in the background ALWAYS suggest using shell commands to stop it; NEVER suggest using Ctrl+C.
                          When suggesting subsequent shell commands ALWAYS format them in shell command blocks.
                          Do NOT perform actions requiring special/admin privileges.
                          Choose terminal commands and scripts optimized for win32 and x64.
                          You can also optionally include the waitForCompletion argument set to false to run the command in the background, without output message.
                          """,
            ExampleToSystemMessage = $"""
                                      For example, to see the git log, you could respond with:
                                      <tool_call_begin> functions.{BuiltInToolEnum.Exec} 
                                      dotnet restore
                                      <tool_call_end>
                                      """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Exec, args)
        },
        new()
        {
            Name = BuiltInToolEnum.FetchUrl,
            Category = ToolCategory.Browser,
            Description = "To fetch the content of a URL, use the fetch_url_content tool.",
            ExampleToSystemMessage = """
                                     For example, to read the contents of a webpage, you might respond with:
                                     <tool_call_begin> functions.fetch_url_content
                                     https://example.com
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.FetchUrl, args)
        },
        
        // Git operations
        new()
        {
            Name = BuiltInToolEnum.GitStatus,
            Category = ToolCategory.ReadFiles,
            Description = "Check git status of the current repository. Shows modified, staged, and untracked files.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.git_status <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitStatus, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GitLog,
            Category = ToolCategory.ReadFiles,
            Description = "View git commit history. Can specify number of commits to display.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.git_log
                                     10
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitLog, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GitDiff,
            Category = ToolCategory.ReadFiles,
            Description = "View git diff for files. Can compare working directory with staged or specific commits.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.git_diff
                                     false
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitDiff, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GitBranch,
            Category = ToolCategory.ReadFiles,
            Description = "List git branches or get current branch information.",
            ExampleToSystemMessage = """
                                     For example:
                                     <tool_call_begin> functions.git_branch <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitBranch, args)
        },
        new()
        {
            Name = BuiltInToolEnum.SwitchMode,
            Category = ToolCategory.ModeSwitch,
            Description = "Switch the current application mode. Available modes: Chat, Agent, Plan. Use this when you need tools from another mode.",
            ExampleToSystemMessage = """
                                     For example, to switch to Agent mode:
                                     <tool_call_begin> functions.switch_mode
                                     Agent
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.SwitchMode, args)
        },
        
        // Skills operations
        new()
        {
            Name = BuiltInToolEnum.ReadSkillContent,
            Category = ToolCategory.ReadFiles,
            Description = """
                          Load the full content of a skill when you need detailed instructions.
                          Skills are pre-listed in your system prompt with name and description.
                          Use this tool only when you determine a skill is relevant to the current task.
                          """,
            ExampleToSystemMessage = """
                                     For example, to load a specific skill:
                                     <tool_call_begin> functions.read_skill_content
                                     .agent/skills/debugging/SKILL.md
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadSkillContent, args)
        },
        new()
        {
            Name = BuiltInToolEnum.DeleteFile,
            Category = ToolCategory.DeleteFiles,
            Description = "To delete a file, use the delete_file tool with the relative or absolute filepath.",
            ExampleToSystemMessage = """
                                     For example, to delete a file located at 'path\\to\\file.cs', you would respond with:
                                     <tool_call_begin> functions.delete_file
                                     \path\to\file.cs
                                     <tool_call_end>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.DeleteFile, args)
        },
        new()
        {
            Name = BuiltInToolEnum.McpStartProcess,
            Category = ToolCategory.Mcp,
            Description = "Start a new MCP server process.",
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.McpStartProcess, args)
        },
        new()
        {
            Name = BuiltInToolEnum.McpStopProcess,
            Category = ToolCategory.Mcp,
            Description = "Stop a running MCP server process.",
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.McpStopProcess, args)
        },
        new()
        {
            Name = BuiltInToolEnum.McpSendMessage,
            Category = ToolCategory.Mcp,
            Description = "Send a JSON-RPC message to an MCP server.",
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.McpSendMessage, args)
        },
        new()
        {
            Name = BuiltInToolEnum.McpReadMessage,
            Category = ToolCategory.Mcp,
            Description = "Read a JSON-RPC message from an MCP server.",
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.McpReadMessage, args)
        },
        new()
        {
            Name = BuiltInToolEnum.McpGetTools,
            Category = ToolCategory.Mcp,
            Description = "Get the list of tools provided by an MCP server.",
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.McpGetTools, args)
        }
    ];
}
