using UIBlazor.Services;
using UIBlazor.Services.Settings;

namespace UIBlazor.Agents;

public class BuiltInAgent(IVsBridge vsBridge, ISkillService skillService, IInternalExecutor internalExecutor)
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

                                     Or to read the entire files:
                                     <function name="{BuiltInToolEnum.ReadFiles}">
                                     path/to/file.cs
                                     path/to/file2.cs
                                     </function>
                                     """,
            NativeTool = BuiltInToolDefs.MapMethodToTool(typeof(BuiltInToolDefs).GetMethod(nameof(BuiltInToolDefs.ReadFiles))),
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, args, c)
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadOpenFile, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.CreateFile,
            DisplayName = SharedResource.ToolCreateFile,
            Category = ToolCategory.WriteFiles,
            Description = "To create a NEW file with the relative or absolute filepath and new contents. Note: The old file will be overwritten if it exists. Use this when you need to make large changes to a single file and it's easier to recreate it.",
            ExampleToSystemMessage = $"""
                                     For example, to create a file located at 'path\to\file.cs', you would respond with:
                                     <function name="{BuiltInToolEnum.CreateFile}">
                                     \path\to\file.cs
                                     Contents of the file.
                                     And second line of this file.
                                     </function>
                                     """,
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.CreateFile, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.ApplyDiff,
            DisplayName = SharedResource.ToolApplyDiff,
            Category = ToolCategory.WriteFiles,
            Description = $"""
                Performs precise, surgical modifications to a file using SEARCH/REPLACE blocks. 
                Use this tool to modify existing code with 100% accuracy.

                STRUCTURE:
                [file_path]
                <<<<<<< SEARCH [:start_line:]
                [exact content to find]
                =======
                [new content to replace with]
                >>>>>>> REPLACE

                OPTIONAL PARAMETERS:
                - :start_line:: A hint for the line number to speed up searching. Example: `<<<<<<< SEARCH :10:`

                CRITICAL RULES:
                1. EXACT MATCH: The SEARCH block must match the file content exactly (including spaces, tabs, and indentation).
                2. BREVITY: Keep each SEARCH block under 15 lines. If the change is larger, use multiple consecutive SEARCH/REPLACE blocks for one file.
                3. EFFICIENCY: Combine all related changes for a single file into one {BuiltInToolEnum.ApplyDiff} call with serial SEARCH/REPLACE blocks separated by a newline.
                4. INTEGRITY: Ensure syntax balance (brackets, quotes) is maintained after the replacement.
                5. UNCERTAINTY: If you don't have the exact text, you MUST use {BuiltInToolEnum.ReadFiles} first.
                """,
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.ApplyDiff}">
                                     C:\path\to\file.cs
                                     <<<<<<< SEARCH :10:
                                     old code
                                     =======
                                     new code
                                     >>>>>>> REPLACE
                                     </function>
                                     
                                     Example for multi replacments in one file:
                                     <function name="{BuiltInToolEnum.ApplyDiff}">
                                     C:\path\to\file.cs
                                     <<<<<<< SEARCH
                                     old code
                                     =======
                                     new code
                                     >>>>>>> REPLACE
                                     
                                     <<<<<<< SEARCH :40:
                                         var z = "old code";
                                     =======
                                         // this is new code
                                         var isNew = true;
                                         var z = isNew ? "new code" : "old code";
                                     >>>>>>> REPLACE
                                     </function>
                                     """,
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ApplyDiff, args, c)
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.SearchFiles, args, c)
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GrepSearch, args, c)
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Dir, args, c)
        },
        
        // Project and build
        new()
        {
            Name = BuiltInToolEnum.Build,
            DisplayName = SharedResource.ToolBuild,
            Category = ToolCategory.ReadFiles,
            Description = "To build solution in Visual Studio. With action - Build, Rebuild or Clean. When any errors returns errors list.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.Build}">
                                     build
                                     </function>
                                     """,
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Build, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.RunTests,
            DisplayName = SharedResource.ToolRunTests,
            Category = ToolCategory.ReadFiles,
            Description = "To run all tests in solution. When any errors returns errors list. Note: the solution build will be triggered automatically when this tool is called.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.RunTests}">
                                     </function>
                                     """,
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.RunTests, args, c)
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetErrors)
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetProjectInfo, args, c)
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetSolutionStructure)
        },
        
        // Execution
        new()
        {
            Name = BuiltInToolEnum.Exec,
            DisplayName = SharedResource.ToolExec,
            Category = ToolCategory.Execution,
            Description = """
                          To run a terminal command.
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Exec, args, c)
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
                                     <function name="{BuiltInToolEnum.GitStatus}">
                                     </function>
                                     """,
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitStatus, args, c)
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitLog, args, c)
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitDiff, args, c)
        },
        new()
        {
            Name = BuiltInToolEnum.GitBranch,
            DisplayName = SharedResource.ToolGitBranches,
            Category = ToolCategory.ReadFiles,
            Description = "List git branches or get current branch information.",
            ExampleToSystemMessage = $"""
                                     For example:
                                     <function name="{BuiltInToolEnum.GitBranch}">
                                     </function>
                                     """,
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GitBranch, args, c)
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
            ExecuteAsync = (args, c) => internalExecutor.ExecuteToolAsync(BasicEnum.SwitchMode, args, c)
        },

        // Skills
        new()
        {
            Name = BasicEnum.ReadSkillContent,
            DisplayName = SharedResource.ToolReadSkillContent,
            Category = ToolCategory.ReadFiles,
            Description = """
                          Load the full content of a skill when you need detailed instructions.
                          Skills are pre-listed in your system prompt with name and description.
                          Use this tool only when you determine a skill is relevant to the current task.
                          For parameter use skill name.
                          """,
            ExampleToSystemMessage = $"""
                                     For example, to load a specific skill:
                                     <function name="{BasicEnum.ReadSkillContent}">
                                     Example skill name
                                     </function>
                                     """,
            ExecuteAsync = skillService.LoadSkillContentMarkDownAsync
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
            ExecuteAsync = (args, c) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.DeleteFile, args, c)
        },

        // TODO
        // User interaction
        //new()
        //{
        //    Name = BasicEnum.AskUser,
        //    DisplayName = SharedResource.ToolAskUser,
        //    Category = ToolCategory.ReadFiles,
        //    Description = """
        //                  Ask the user a question and present options for them to choose from.
        //                  Use this when you need clarification or user input to proceed.
        //                  The user can select one of the provided options or enter their own answer.
        //                  Parameters:
        //                  - question: The question to ask the user - first line
        //                  - options: A list of options for the user to choose from (one per line)
        //                  """,
        //    ExampleToSystemMessage = $"""
        //                             For example, to ask which file to open:
        //                             <function name="{BasicEnum.AskUser}">
        //                             Which file would you like me to open?
        //                             src/main.cs
        //                             src/utils.cs
        //                             src/config.cs
        //                             </function>
        //                             """,
        //    ExecuteAsync = (args, c) => internalExecutor.ExecuteToolAsync(BasicEnum.AskUser, args, c)
        //}
    ];
}
