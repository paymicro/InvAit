using Shared.Contracts;
using UIBlazor.VS;

namespace UIBlazor.Agents;

public class BuiltInAgent(IVsBridge vsBridge)
{
    public readonly IReadOnlyList<Tool> Tools =
    [
        new()
        {
            Name = BuiltInToolEnum.ReadFiles,
            Description = "Request to read the contents of one or more files. The tool outputs line-numbered content (e.g. \"1 | const x = 1\") for easy reference when creating diffs or discussing code.",
            ExampleToSystemMessage = """
                                     For example, to read 2 files, you would respond with this:
                                     <|tool_call_begin|> functions.read_files:1 <|tool_call_argument_begin|> {"files": [ \"path/to/the_file1.txt\", \"path/to/the_file2.txt\" ]} <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadFiles, args),
            ApprovalNeeded = false
        },
        new()
        {
            Name = BuiltInToolEnum.ReadOpenFile,
            Description = """
                          To view the user's currently open file, use the read_currently_open_file tool. The tool outputs line-numbered content (e.g. "1 | const x = 1")
                          If the user is asking about a file and you don't see any code, use this to check the current file
                          """,
            ExampleToSystemMessage = """
                                     For example
                                     <|tool_call_begin|> functions.read_currently_open_file:1 <|tool_call_argument_begin|> <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ReadOpenFile, args),
            ApprovalNeeded = false
        },
        new()
        {
            Name = BuiltInToolEnum.CreateFile,
            Description = "To create a NEW file, use the create_new_file tool with the relative filepath and new contents.",
            ExampleToSystemMessage = """
                                     For example, to create a file located at 'path/to/file.txt', you would respond with:
                                     <|tool_call_begin|> functions.create_new_file:1 <|tool_call_argument_begin|> {"filepath": "path/to/file.txt", "contents": "Contents of the file"} <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.CreateFile, args)
        },
        new()
        {
            Name = BuiltInToolEnum.Exec,
            Description = """
                          To run a terminal command, use the execute_command tool in
                          The shell is not stateful and will not remember any previous commands.
                          When a command is run in the background ALWAYS suggest using shell commands to stop it; NEVER suggest using Ctrl+C.
                          When suggesting subsequent shell commands ALWAYS format them in shell command blocks.
                          Do NOT perform actions requiring special/admin privileges.
                          Choose terminal commands and scripts optimized for win32 and x64.
                          You can also optionally include the waitForCompletion argument set to false to run the command in the background, without output message.
                          """,
            ExampleToSystemMessage = """
                                     For example, to see the git log, you could respond with:
                                     <|tool_call_begin|> functions.execute_command:1 <|tool_call_argument_begin|> {"exe": "dotnet", "command": "restore"} <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Exec, args)
        },
        new()
        {
            Name = BuiltInToolEnum.SearchFiles,
            Description = "To return a list of files with patches in solution directory based on a search regex pattern, use the search_files tool.",
            ExampleToSystemMessage = """
                                     For example:
                                     <|tool_call_begin|> functions.search_files:1 <|tool_call_argument_begin|> {"regex": "^.*\.cs$"} <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.SearchFiles, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GrepSearch,
            Description = "To perform a grep search within the project, call the grep_search tool with the regex pattern to match.",
            ExampleToSystemMessage = """
                                     For example:
                                     <|tool_call_begin|> functions.grep_search:1 <|tool_call_argument_begin|> {"regex": "^.*?main_services.*"} <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GrepSearch, args)
        },
        new()
        {
            Name = BuiltInToolEnum.Ls,
            Description = "To list files and folders in a given directory, call the ls tool with \"dirPath\" and \"recursive\".",
            ExampleToSystemMessage = """
                                     For example:
                                     <|tool_call_begin|> functions.ls:1 <|tool_call_argument_begin|> {"dirPath": "path/to/dir", "recursive": false} <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Ls, args)
        },
        new()
        {
            Name = BuiltInToolEnum.FetchUrl,
            Description = "To fetch the content of a URL, use the fetch_url_content tool.",
            ExampleToSystemMessage = """
                                     For example, to read the contents of a webpage, you might respond with:
                                     <|tool_call_begin|> functions.fetch_url_content:1 <|tool_call_argument_begin|> {"url": "https://example.com"} <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.FetchUrl, args)
        },
        new()
        {
            Name = BuiltInToolEnum.ApplyDiff,
            Description = """
                          Request to apply PRECISE, TARGETED modifications to an existing file by searching for specific sections of content and replacing them. This tool is for SURGICAL EDITS ONLY - specific changes to existing code.
                          You can perform multiple distinct search and replace operations within a single `apply_diff` call by providing multiple SEARCH/REPLACE blocks in the `diff` parameter. This is the preferred way to make several targeted changes efficiently.
                          The SEARCH section must exactly match existing content including whitespace and indentation.
                          If you're not confident in the exact content to search for, use the read_file tool first to get the exact content.
                          When applying the diffs, be extra careful to remember to change any closing brackets or other syntax that may be affected by the diff farther down in the file.
                          ALWAYS make as many changes in a single 'apply_diff' request as possible using multiple SEARCH/REPLACE blocks
                          """,
            ExampleToSystemMessage = """"
                                     If you're not confident in the exact content to search for, use the read_file tool first to get the exact content.
                                     `start_line` - (required) The line number 0-indexed of original content where the search block starts.
                                     `diff` - The search/replace block defining the changes.
                                     Example:

                                     Original file:
                                     ```
                                     1 | def calculate_total(items):
                                     2 |     total = 0
                                     3 |     for item in items:
                                     4 |         total += item
                                     5 |     return total
                                     ```

                                     Search/Replace content:
                                     ```
                                     <<<<<<< SEARCH
                                     :start_line:1
                                     -------
                                     def calculate_total(items):
                                         total = 0
                                         for item in items:
                                             total += item
                                         return total
                                     =======
                                     def calculate_total(items):
                                         """Calculate total with 10% markup"""
                                         return sum(item * 1.1 for item in items)
                                     >>>>>>> REPLACE
                                     ```

                                     Search/Replace content with multiple edits:
                                     ```
                                     <<<<<<< SEARCH
                                     :start_line:1
                                     -------
                                     def calculate_total(items):
                                         sum = 0
                                     =======
                                     def calculate_sum(items):
                                         sum = 0
                                     >>>>>>> REPLACE

                                     <<<<<<< SEARCH
                                     :start_line:4
                                     -------
                                             total += item
                                         return total
                                     =======
                                             sum += item
                                         return sum
                                     >>>>>>> REPLACE
                                     ```

                                     Usage:
                                     <apply_diff>
                                     <path>File path here</path>
                                     <diff>
                                     Your search/replace content here
                                     You can use multi search/replace block in one diff block, but make sure to include the line numbers for each block.
                                     Only use a single line of '=======' between search and replacement content, because multiple '=======' will corrupt the file.
                                     </diff>
                                     </apply_diff>

                                     For example, you could respond with:
                                     <|tool_call_begin|> functions.apply_diff:1 <|tool_call_argument_begin|>
                                     { "path": "path/to/file.txt", "diff": "Your search/replace content here
                                     You can use multi search/replace block in one diff block, but make sure to include the line numbers for each block.
                                     Only use a single line of '=======' between search and replacement content, because multiple '=======' will corrupt the file." }
                                     <|tool_call_end|>
                                     """",
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.ApplyDiff, args)
        },
        new()
        {
            Name = BuiltInToolEnum.Build,
            Description = "To build solution in Visual Studio. With action - Build, Rebuild or Clean. When any errors returns errors list.",
            ExampleToSystemMessage = """
                                     For example:
                                     <|tool_call_begin|> functions.build_solution:1 <|tool_call_argument_begin|> {"action": "build"} <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.Build, args)
        },
        new()
        {
            Name = BuiltInToolEnum.GetErrors,
            Description = "To get error list of current solution and current file from Visual Studio.",
            ExampleToSystemMessage = """
                                     For example:
                                     <|tool_call_begin|> functions.get_error_list:1 <|tool_call_argument_begin|> {} <|tool_call_end|>
                                     """,
            ExecuteAsync = (args) => vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetErrors)
        }
    ];
}
