namespace UIBlazor.Agents;

public class BuiltInAgent(IVsBridge vsBridge)
{
    public IReadOnlyList<Tool> Tools =
    [
        // File operations
        new()
        {
            Name = BuiltInToolEnum.ReadFiles,
            DisplayName = SharedResource.ToolReadFiles,
            Category = ToolCategory.ReadFiles,
            Description = "Request to read the contents of one or more files. Use start_line and line_count to read specific parts of large files.",
            ExampleToSystemMessage = $"""
                                     For example, to read a specific range:
                                     <function name="{BuiltInToolEnum.ReadFiles}">
                                     path/to/large_file.cs
                                     start_line
                                     100
                                     line_count
                                     50
                                     </function>

                                     Or to read the entire file:
                                     <function name="{BuiltInToolEnum.ReadFiles}">
                                     path/to/file.cs
                                     path/to/file2.cs
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, args)
        },
        new()
        {
            Name = BuiltInToolEnum.ReadOpenFile,
            DisplayName = SharedResource.ToolReadOpenFile,
            Category = ToolCategory.ReadFiles,
            Description = $"""
                          To view the user's currently open file, use the {BuiltInToolEnum.ReadOpenFile} tool. 
                          The tool returns the absolute file path and its line-numbered content (e.g. "1 | const x = 1").
                          If the user is asking about a file and you don't see any code, use this to check the current file.
                          """,
            ExampleToSystemMessage = $"""
                                     For example
                                     <function name="{BuiltInToolEnum.ReadOpenFile}"></function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadOpenFile, args)
        },
        new()
        {
            Name = BuiltInToolEnum.CreateFile,
            DisplayName = SharedResource.ToolCreateFile,
            Category = ToolCategory.WriteFiles,
            Description = "To create a NEW file with the relative or absolute filepath and new contents.",
            ExampleToSystemMessage = $"""
                                     For example, to create a file located at 'path\to\file.cs', you would respond with:
                                     <function name="{BuiltInToolEnum.CreateFile}">
                                     \path\to\file.cs
                                     Contents of the file.
                                     And second line of this file.
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.CreateFile, args)
        },
        new()
        {
            Name = BuiltInToolEnum.ApplyDiff,
            DisplayName = SharedResource.ToolApplyDiff,
            Category = ToolCategory.WriteFiles,
            Description = $"""
                          Request to apply PRECISE, TARGETED modifications to an existing file by searching for specific sections of content and replacing them. This tool is for SURGICAL EDITS ONLY - specific changes to existing code.
                          You can perform multiple distinct search and replace operations within a single `{BuiltInToolEnum.ApplyDiff}` call by providing multiple SEARCH/REPLACE blocks in the `diff` parameter. This is the preferred way to make several targeted changes efficiently.
                          The SEARCH section must exactly match existing content including whitespace and indentation.
                          If you're not confident in the exact content to search for, use the {BuiltInToolEnum.ReadFiles} tool first to get the exact content.
                          When applying the diffs, be extra careful to remember to change any closing brackets or other syntax that may be affected by the diff farther down in the file.
                          ALWAYS make as many changes in a single '{BuiltInToolEnum.ApplyDiff}' request as possible using multiple SEARCH/REPLACE blocks.
                          An optional ":start_line:". The search will be 5 lines up and down.
                          """,
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.ApplyDiff}">
                                     C:\path\to\file.cs
                                     :start_line:10
                                     <<<<<<< SEARCH
                                     old code
                                     =======
                                     new code
                                     >>>>>>> REPLACE
                                     </function>
                                     
                                     <function name="{BuiltInToolEnum.ApplyDiff}">
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
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ApplyDiff, args)
        },
        
        // Search and navigation
        new()
        {
            Name = BuiltInToolEnum.SearchFiles,
            DisplayName = SharedResource.ToolSearchFiles,
            Category = ToolCategory.ReadFiles,
            Description = "To return a list of files with patches in solution directory based on a search regex pattern, use the search_files tool.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.SearchFiles}">
                                     ^.*\.cs$
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.SearchFiles, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GrepSearch,
            DisplayName = SharedResource.ToolGrepSearch,
            Category = ToolCategory.ReadFiles,
            Description = "To perform a grep search within the project, call the grep_search tool with the regex pattern to match.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.GrepSearch}">
                                     ^.*?main_services.*
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GrepSearch, args)
        },
        new()
        {
            Name = BuiltInToolEnum.Dir,
            DisplayName = SharedResource.ToolDir,
            Category = ToolCategory.ReadFiles,
            Description = "To list files and folders in a given directory, call this tool with \"dirPath\" and \"recursive\".",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.Dir}">
                                     C:\path\to\dir
                                     false
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Dir, args)
        },
        
        // Project and build
        new()
        {
            Name = BuiltInToolEnum.Build,
            DisplayName = SharedResource.ToolBuild,
            Category = ToolCategory.Execution,
            Description = "To build solution in Visual Studio. With action - Build, Rebuild or Clean. When any errors returns errors list.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.Build}">
                                     build
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Build, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GetErrors,
            DisplayName = SharedResource.ToolGetErrors,
            Category = ToolCategory.ReadFiles,
            Description = "To get error list of current solution and current file from Visual Studio.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.GetErrors}"></function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetErrors)
        },
        new()
        {
            Name = BuiltInToolEnum.GetProjectInfo,
            DisplayName = SharedResource.ToolGetProjectInfo,
            Category = ToolCategory.ReadFiles,
            Description = "Get information about the solution and projects. Returns list of projects, their types, target frameworks, and file structure.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.GetProjectInfo}"></function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetProjectInfo, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GetSolutionStructure,
            DisplayName = SharedResource.ToolGetSolutionStructure,
            Category = ToolCategory.ReadFiles,
            Description = "Get a tree-like structure of the entire solution, including projects, folders, and files.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.GetSolutionStructure}"></function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure)
        },
        
        // Execution
        new()
        {
            Name = BuiltInToolEnum.Exec,
            DisplayName = SharedResource.ToolExec,
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
                                      <function name="{BuiltInToolEnum.Exec}"> 
                                      dotnet restore
                                      </function>
                                      """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Exec, args)
        },
        
        // Git operations
        new()
        {
            Name = BuiltInToolEnum.GitStatus,
            DisplayName = SharedResource.ToolGitStatus,
            Category = ToolCategory.ReadFiles,
            Description = "Check git status of the current repository. Shows modified, staged, and untracked files.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.GitStatus}"></function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitStatus, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GitLog,
            DisplayName = SharedResource.ToolGitLog,
            Category = ToolCategory.ReadFiles,
            Description = "View git commit history. Can specify number of commits to display.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.GitLog}">
                                     10
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitLog, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GitDiff,
            DisplayName = SharedResource.ToolGitDiff,
            Category = ToolCategory.ReadFiles,
            Description = "View git diff for files. Can compare working directory with staged or specific commits.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.GitDiff}">
                                     false
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitDiff, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GitBranch,
            DisplayName = SharedResource.ToolGitBranches,
            Category = ToolCategory.ReadFiles,
            Description = "List git branches or get current branch information.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.GitBranch}"></function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitBranch, args)
        },
        new()
        {
            Name = BasicEnum.SwitchMode,
            DisplayName = SharedResource.ToolSwitchMode,
            Category = ToolCategory.ModeSwitch,
            Description = "Switch the current application mode. Available modes: Chat, Agent, Plan. Use this when you need tools from another mode.",
            ExampleToSystemMessage = $"""
                                     For example, to switch to Agent mode:
                                     <function name="{BasicEnum.SwitchMode}">
                                     Agent
                                     </function>
                                     """,
            // TODO тут нужен другой класс, например internalToolExec и туда же отправить браузер
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BasicEnum.SwitchMode, args)
        },
        
        // Skills operations
        new()
        {
            Name = BasicEnum.ReadSkillContent,
            DisplayName = SharedResource.ToolReadSkillContent,
            Category = ToolCategory.ReadFiles,
            Description = """
                          Load the full content of a skill when you need detailed instructions.
                          Skills are pre-listed in your system prompt with name and description.
                          Use this tool only when you determine a skill is relevant to the current task.
                          """,
            ExampleToSystemMessage = $"""
                                     For example, to load a specific skill:
                                     <function name="{BasicEnum.ReadSkillContent}">
                                     .agent/skills/debugging/SKILL.md
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BasicEnum.ReadSkillContent, args)
        },
        new()
        {
            Name = BuiltInToolEnum.DeleteFile,
            DisplayName = SharedResource.ToolDeleteFile,
            Category = ToolCategory.DeleteFiles,
            Description = "To delete a file, use this tool with the relative or absolute filepath.",
            ExampleToSystemMessage = $"""
                                     For example, to delete a file located at 'path\\to\\file.cs', you would respond with:
                                     <function name="{BuiltInToolEnum.DeleteFile}">
                                     C:\path\to\file.cs
                                     </function>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.DeleteFile, args)
        }
    ];
}
