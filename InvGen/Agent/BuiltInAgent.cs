using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Documents;
using EnvDTE;
using EnvDTE80;
using InvGen.Utils;
using Shared.Contracts;
using Process = System.Diagnostics.Process;
using Shell = Microsoft.VisualStudio.Shell;
using Toolkit = Community.VisualStudio.Toolkit;
using VS = Community.VisualStudio.Toolkit.VS;

namespace InvGen.Agent;

public class BuiltInAgent
{
    public async Task<VsResponse> ExecuteAsync(VsRequest vsRequest)
    {
        try
        {
            var response = vsRequest.Action switch
            {
                BuiltInToolEnum.ReadFiles => await ReadFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.ReadOpenFile => await ReadCurrentlyOpenFileAsync(),
                BuiltInToolEnum.CreateFile => await CreateNewFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Exec => await ExecAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.SearchFiles => await SearchFilesAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GrepSearch => await GrepSearchAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Ls => await ListDirectoryAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.FetchUrl => await FetchUrlContentAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.ApplyDiff => await ApplyDiffAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Build => await BuildSolutionAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GetErrors => await GetErrorListAsync(),
                BuiltInToolEnum.GetProjectInfo => await GetProjectInfoAsync(),
                BuiltInToolEnum.GitStatus => await GitStatusAsync(),
                BuiltInToolEnum.GitLog => await GitLogAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GitDiff => await GitDiffAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GitBranch => await GitBranchAsync(),
                _ => new VsResponse { Success = false, Error = "Unknown action" }
            };

            response.CorrelationId = vsRequest.CorrelationId;

            return response;
        }
        catch (Exception ex)
        {
            await Logger.LogAsync($"BuiltInAgent execute exception {ex.Message}", "ERROR");
            return new VsResponse
            {
                Success = false,
                CorrelationId = vsRequest.CorrelationId,
                Error = ex.Message
            };
        }
    }

    private async Task<VsResponse> ReadCurrentlyOpenFileAsync()
    {
        var docView = await VS.Documents.GetActiveDocumentViewAsync();
        if (docView?.Document?.FilePath == null)
            return new VsResponse
            {
                Success = false,
                Error = "No active document"
            };

        return await ReadFileAsync(new Dictionary<string, object>
        {
            { "files", JsonUtils.Serialize(new List<string> { docView.Document.FilePath }) }
        });
    }

    private async Task<VsResponse> ReadFileAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var filesInString = args.GetString("files");
        var files = JsonUtils.Deserialize<List<string>>(filesInString);

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var stingBuilder = new StringBuilder();
        var isSuccess = true;
        foreach (var fileName in files)
        {
            var filepath = GetAbsolutePath(fileName, solutionPath);
            if (!File.Exists(filepath))
            {
                stingBuilder.AppendLine($"File \"{fileName}\" doesn't exist.");
                break;
            }

            stingBuilder.AppendLine($"\"{fileName}\" content");

            try
            {
                var lineNum = 0;
                foreach (var readLine in File.ReadLines(filepath))
                {
                    stingBuilder.AppendLine($"{++lineNum} | {readLine}");
                }
            }
            catch (Exception e)
            {
                Logger.Log(e.Message);
                stingBuilder.AppendLine($"Error {e.Message}");
                isSuccess = false;
            }
        }

        return new VsResponse
        {
            Success = isSuccess,
            Payload = stingBuilder.ToString()
        };
    }

    private async Task<VsResponse> CreateNewFileAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var filepath = GetAbsolutePath(args.GetString("filepath"), solutionPath);
        var contents = args.GetString("contents");

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var directory = Path.GetDirectoryName(filepath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filepath, contents);
            return new VsResponse
            {
                Payload = $"File {args.GetString("filepath")} created successfully."
            };
        }
        catch (Exception e)
        {
            await Logger.LogAsync(e.Message);
            return new VsResponse
            {
                Success = false,
                Error = e.Message
            };
        }
    }

    private async Task<VsResponse> ExecAsync(IReadOnlyDictionary<string, object> args)
    {
        var exe = args.GetString("exe").ToLower();
        var command = args.GetString("command");
        var waitForCompletion = !args.ContainsKey("waitForCompletion") || args.GetBool("waitForCompletion");

        var solutionPath = await GetSolutionPathAsync();

        if (exe is not ("cmd" or "powershell" or "dotnet" or "git"))
        {
            return new VsResponse
            {
                Success = false,
                Payload = $"{exe} is unsupported."
            };
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return new VsResponse
            {
                Success = false,
                Payload = "Command should be not empty."
            };
        }

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = exe is "powershell"
                ? $"-Command \"{command}\""
                : command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = solutionPath
        };

        using var process = Process.Start(startInfo);
        if (process == null)
            return new VsResponse
            {
                Success = false,
                Payload = "Failed to start process"
            };

        if (waitForCompletion)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask);
            await Task.Run(() => process.WaitForExit());

            var error = await errorTask;
            var output = await outputTask;
            var isSuccess = string.IsNullOrEmpty(error);
            return new VsResponse
            {
                Success = isSuccess,
                Payload = isSuccess ? $"Command executed successfully: {output}" : $"Error: {error}",
                Error = error
            };
        }

        return new VsResponse
        {
            Payload = "Command started in background"
        };
    }

    private async Task<VsResponse> SearchFilesAsync(IReadOnlyDictionary<string, object> args)
    {
        var pattern = args.GetString("regex");
        if (string.IsNullOrEmpty(pattern))
        {
            return new VsResponse
            {
                Success = false,
                Payload = "Regex pattern should be not empty."
            };
        }

        var solutionPath = await GetSolutionPathAsync();
        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var regex = new Regex(pattern, RegexOptions.Compiled);
        var files = (await GetAllSolutionFilesAsync()).Where(f => regex.IsMatch(f))
            .Select(f => MakeRelativeToSolution(f, solutionPath))
            .ToArray();

        return new VsResponse
        {
            Payload = files.Length == 0 ? "Nothing found." : string.Join(", ", files)
        };
    }

    private async Task<VsResponse> GrepSearchAsync(IReadOnlyDictionary<string, object> args)
    {
        var query = args.GetString("query");
        if (string.IsNullOrEmpty(query))
        {
            return new VsResponse
            {
                Success = false,
                Payload = "Parameter 'query' is invalid."
            };
        }

        var results = new List<string>();
        var solutionPath = await GetSolutionPathAsync();

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var files = await GetAllSolutionFilesAsync();
        var regex = new Regex(query, RegexOptions.Multiline);

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var matches = regex.Matches(content);
                if (matches.Count <= 0)
                    continue;
                var relativePath = MakeRelativeToSolution(file, solutionPath);
                results.Add($"{relativePath} - {matches.Count} matches");
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        return new VsResponse
        {
            Payload = results.Count == 0 ? "Nothing found." : string.Join("\n", results)
        };
    }

    private async Task<VsResponse> ListDirectoryAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var dirPath = GetAbsolutePath(args.GetString("dirPath"), solutionPath);
        var recursive = args.GetBool("recursive");

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!Directory.Exists(dirPath))
            return new VsResponse
            {
                Success = false,
                Error = $"Directory {args.GetString("dirPath")} doesn't exist",
            };

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var items = string.Join(Environment.NewLine, Directory.GetFileSystemEntries(dirPath, "*", searchOption)
            .Select(f => MakeRelativeToSolution(f, solutionPath)));

        return new VsResponse
        {
            Payload = $"Listed directory {args.GetString("dirPath")}{Environment.NewLine}{items}"
        };
    }

    private async Task<VsResponse> FetchUrlContentAsync(IReadOnlyDictionary<string, object> args)
    {
        var url = args.GetString("url");
        if (string.IsNullOrEmpty(url))
        {
            return new VsResponse
            {
                Success = false,
                Error = "Error: url is empty."
            };
        }

        try
        {
            var request = WebRequest.CreateHttp(url);
            request.Method = "GET";
            request.UserAgent = "InvGen";
            request.Timeout = 3000;

            using var response = await request.GetResponseAsync();
            using var stream = response.GetResponseStream();
            if (stream == null)
            {
                return new VsResponse
                {
                    Success = false,
                    Error = "Error: response stream is null."
                };
            }

            var buffer = new char[1000];
            using var reader = new StreamReader(stream);
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, read);
            if (content.Length >= 1000)
            {
                content = content.Substring(0, 1000) + " ...";
            }

            return new VsResponse
            {
                Payload = content
            };
        }
        catch (WebException ex)
        {
            return new VsResponse
            {
                Success = false,
                Error = $"Web error: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new VsResponse
            {
                Success = false,
                Error = $"Error fetching URL: {ex.Message}"
            };
        }
    }

    private async Task<VsResponse> ApplyDiffAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var inputFileName = args.GetString("path");
        var filepath = GetAbsolutePath(inputFileName, solutionPath);
        var argsDifflines = args.GetString("diff").Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var parser = new UniversalDiffParser();
        var replacements = parser.Parse(argsDifflines);

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!File.Exists(filepath))
            return new VsResponse { Success = false, Error = "File doesn't exist." };

        if (replacements == null || replacements.Count == 0)
            return new VsResponse { Success = false, Error = "No valid replacements found in diff." };

        var lines = File.ReadAllLines(filepath).ToList();
        var totalReplacements = 0;
        var appliedReplacements = new List<string>();

        replacements = replacements.OrderByDescending(r => r.StartLine).ToList();
        var isError = false;

        foreach (var rep in replacements)
        {
            var actualStart = parser.FindInFile(lines, rep.Search, rep.StartLine, 4);
            if (actualStart == -1)
            {
                return new VsResponse
                {
                    Success = false,
                    Error = $"Failed to apply diff at line {rep.StartLine} in file {inputFileName}"
                };
            }

            // Replace from actualStart
            lines.RemoveRange(actualStart, rep.Search.Count);
            lines.InsertRange(actualStart, rep.Replace);

            totalReplacements++;
        }

        //foreach (var replacement in replacements)
        //{
        //    var startIndex = replacement.StartLine - 1;
        //    var endLine = replacement.StartLine + replacement.Search.Count - 1;

        //    if (replacement.StartLine < 1 || endLine > lines.Count)
        //    {
        //        continue;
        //    }

        //    var currentLines = lines.Skip(startIndex - 4).Take(replacement.Search.Count + 6).ToList();
        //    var diffIndex = FindSubarrayIndex(currentLines, replacement.Search);
        //    if (diffIndex == -1)
        //    {
        //        continue;
        //    }

        //    startIndex += diffIndex - 4;

        //    // Replace lines
        //    lines.RemoveRange(startIndex, currentLines.Count);
        //    lines.InsertRange(startIndex, replacement.Replace);

        //    totalReplacements++;
        //    appliedReplacements.Insert(0, $"{startIndex}-{endLine}");
        //}

        if (totalReplacements == 0)
        {
            return new VsResponse { Success = false, Error = "No valid replacements found." };
        }

        try
        {
            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetFileName(filepath));
            File.Copy(filepath, tempFile, true);
            File.WriteAllLines(filepath, lines);
            await FileDiffAsync(tempFile, filepath, "old", "new");
            Logger.Log($"Applied {totalReplacements} replacements to {inputFileName}: {string.Join(", ", appliedReplacements)}");
        }
        catch (Exception e)
        {
            await Logger.LogAsync(e.Message);
            return new VsResponse
            {
                Payload = $"Changes successfully applied to {inputFileName}.\r\nLooks good!"
            };
        }

        return new VsResponse
        {
            Payload = $"Changes successfully applied to {inputFileName}.\r\nLooks good!. Applied {totalReplacements} replacements"
        };
    }

    public int FindSubarrayIndex(List<string> bigArray, List<string> smallArray)
    {
        if (bigArray == null || smallArray == null || bigArray.Count < smallArray.Count)
        {
            return -1;
        }

        for (int i = 0; i <= bigArray.Count - smallArray.Count; i++)
        {
            // Берем часть bigArray, начиная с i, длиной как smallArray
            // и сравниваем её с smallArray
            if (bigArray.Skip(i).Take(smallArray.Count).SequenceEqual(smallArray))
            {
                return i; // Нашли, возвращаем индекс
            }
        }
        return -1; // Не нашли
    }

    private async Task<VsResponse> BuildSolutionAsync(IReadOnlyDictionary<string, object> args)
    {
        var buildAction = (Toolkit.BuildAction)args.GetInt("action");
        var result = await VS.Build.BuildSolutionAsync(buildAction);

        if (!result)
        {
            var errorList = await GetErrorListAsync();

            return new VsResponse
            {
                Success = false,
                Error = $"""
                          Build is failed.

                          {errorList.Error}
                          """
            };
        }

        return new VsResponse
        {
            Payload = "Build is successful."
        };
    }

    private async Task<VsResponse> GetErrorListAsync()
    {
        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Shell.Package.GetGlobalService(typeof(DTE)) as DTE2;
        var errorList = dte?.ToolWindows?.ErrorList?.ErrorItems;
        if (errorList == null)
        {
            return new VsResponse
            {
                Success = false,
                Error = "Error list is null"
            };
        }

        var errors = new List<BuildError>();

        for (var i = 1; i <= errorList.Count; i++)
        {
            var errorItem = errorList.Item(i);
            try
            {
                errors.Add(new BuildError
                {
                    Message = errorItem.Description,
                    FileName = errorItem.FileName,
                    Line = errorItem.Line
                });
            }
            catch
            {
                // safe skip error
            }
        }

        return new VsResponse
        {
            Payload = JsonUtils.Serialize(errors)
        };
    }

    private async Task<VsResponse> GetProjectInfoAsync()
    {
        var solutionPath = await GetSolutionPathAsync();
        var projectInfoList = new List<string>();

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var projects = await VS.Solutions.GetAllProjectsAsync();
        foreach (var project in projects)
        {
            var projectFilePath = project.FullPath;
            var projectName = Path.GetFileNameWithoutExtension(projectFilePath);
            projectInfoList.Add($"Project: {projectName}, Path: {projectFilePath}");
        }

        return new VsResponse
        {
            Payload = projectInfoList.Count == 0 ? "No projects found." : string.Join(Environment.NewLine, projectInfoList)
        };
    }

    private async Task<VsResponse> GitStatusAsync()
    {
        return await ExecGitCommandAsync("status");
    }

    private async Task<VsResponse> GitLogAsync(IReadOnlyDictionary<string, object> args)
    {
        var format = args.GetString("format") ?? "oneline";
        return await ExecGitCommandAsync($"log --pretty={format}");
    }

    private async Task<VsResponse> GitDiffAsync(IReadOnlyDictionary<string, object> args)
    {
        var revisions = args.GetString("revisions");
        return await ExecGitCommandAsync($"diff {revisions}");
    }

    private async Task<VsResponse> GitBranchAsync()
    {
        return await ExecGitCommandAsync("branch --show-current");
    }

    private async Task<VsResponse> ExecGitCommandAsync(string arguments)
    {
        return await ExecAsync(new Dictionary<string, object>() { { "exe", "git" }, { "command", arguments } });
    }

    private async Task FileDiffAsync(string file1, string file2, string file1Title, string file2Title)
    {
        if (string.IsNullOrEmpty(file1Title))
        {
            file1Title = Path.GetFileName(file1);
        }
        if (string.IsNullOrEmpty(file2Title))
        {
            file2Title = Path.GetFileName(file2);
        }
        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Shell.Package.GetGlobalService(typeof(DTE)) as DTE;
        dte?.ExecuteCommand("Tools.DiffFiles", $"\"{file1}\" \"{file2}\" \"{file1Title}\" \"{file2Title}\"");
    }

    private async Task<string> GetSolutionPathAsync()
    {
        var solution = await VS.Solutions.GetCurrentSolutionAsync();
        return solution != null ? Path.GetDirectoryName(solution.FullPath) : Directory.GetCurrentDirectory();
    }

    private string GetAbsolutePath(string relativePath, string solutionPath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            throw new ArgumentException("Path cannot be null or empty.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            return relativePath;
        }

        var path = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar);
        return path.StartsWith(solutionPath) ? path : $"{solutionPath}{Path.DirectorySeparatorChar}{path}";
    }

    private string MakeRelativeToSolution(string fullPath, string solutionPath)
    {
        return !fullPath.StartsWith(solutionPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.Substring(solutionPath.Length + 1);
    }

    /// <summary>
    /// Get list with full file paths included in solution.
    /// </summary>
    private async Task<List<string>> GetAllSolutionFilesAsync()
    {
        var files = new List<string>();
        var projects = await VS.Solutions.GetAllProjectsAsync();
        foreach (var project in projects)
        {
            await WalkItemsAsync(project.Children, files);
        }

        return files;
    }

    private async Task WalkItemsAsync(IEnumerable<Toolkit.SolutionItem> items, List<string> files)
    {
        foreach (var item in items)
        {
            switch (item.Type)
            {
                case Toolkit.SolutionItemType.PhysicalFile when (item as Toolkit.PhysicalFile)?.Extension is not (".zip" or ".bin" or ".dll" or ".exe"):
                    files.Add(item.FullPath);
                    break;
                case Toolkit.SolutionItemType.Project:
                    files.Add(item.FullPath);
                    await WalkItemsAsync(item.Children, files);
                    break;
                case Toolkit.SolutionItemType.PhysicalFolder or Toolkit.SolutionItemType.SolutionFolder:
                    await WalkItemsAsync(item.Children, files);
                    break;
            }
        }
    }

    private class BuildError
    {
        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("file_name")]
        public string FileName { get; set; }

        [JsonPropertyName("line")]
        public int Line { get; set; }
    }
}
