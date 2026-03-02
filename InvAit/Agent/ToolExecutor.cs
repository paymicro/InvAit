using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using InvAit.Utils;
using Microsoft.VisualStudio.Shell;
using Shared.Contracts;
using Shared.Contracts.Mcp;
using Process = System.Diagnostics.Process;
using Shell = Microsoft.VisualStudio.Shell;
using Toolkit = Community.VisualStudio.Toolkit;
using VS = Community.VisualStudio.Toolkit.VS;

namespace InvAit.Agent;

public class ToolExecutor
{
    private readonly ConcurrentDictionary<string, DateTime> _skillsCache = new();
    private readonly McpProcessManager _mcpProcessManager = new();
    public async Task<VsResponse> ExecuteAsync(VsRequest vsRequest)
    {
        try
        {
            var response = vsRequest.Action switch
            {
                BuiltInToolEnum.ReadFiles => await ReadFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.ReadOpenFile => await ReadCurrentlyOpenFileAsync(),
                BuiltInToolEnum.CreateFile => await CreateNewFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.DeleteFile => await DeleteFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Exec => await ExecAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.SearchFiles => await SearchFilesAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GrepSearch => await GrepSearchAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Dir => await ListDirectoryAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.ApplyDiff => await ApplyDiffAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Build => await BuildSolutionAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GetErrors => await GetErrorListAsync(),
                BuiltInToolEnum.GetProjectInfo => await GetProjectInfoAsync(),
                BuiltInToolEnum.GetSolutionStructure => await GetSolutionStructureAsync(),
                BuiltInToolEnum.GitStatus => await GitStatusAsync(),
                BuiltInToolEnum.GitLog => await GitLogAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GitDiff => await GitDiffAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GitBranch => await GitBranchAsync(),
                BasicEnum.SwitchMode => new VsResponse { Success = true, Payload = "Mode switched" },
                BasicEnum.OpenFile => await OpenFileInEditorAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.GetSkillsMetadata => await GetSkillsMetadataAsync(),
                BasicEnum.ReadSkillContent => await ReadSkillContentAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.GetRules => await GetRulesAsync(),
                // MCP
                BasicEnum.McpGetTools => await McpGetToolsAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.McpCallTool => await McpCallToolAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.McpStopAll => await StopAllMcpServersAsync(),
                BasicEnum.ReadMcpSettingsFile => await ReadMcpSettingsAsync(),
                BasicEnum.WriteMcpSettings => await WriteMcpSettingsAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.OpenMcpSettings => await OpenMcpSettingsAsync(),
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

        var response = await ReadFileAsync(new Dictionary<string, object>
        {
            { "param1", docView.Document.FilePath }
        });

        if (response.Success)
        {
            response.Payload = $"Active file: {docView.Document.FilePath}{Environment.NewLine}{Environment.NewLine}{response.Payload}";
        }

        return response;
    }

    private async Task<VsResponse> ReadFileAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var fileParamsList = new List<ReadFileParams>();

        // 1. Try to get structured parameters (file1, file2, ...)
        var fileKeys = args.Keys.Where(k => k.StartsWith("file", StringComparison.OrdinalIgnoreCase)).ToList();
        if (fileKeys.Count > 0)
        {
            foreach (var key in fileKeys.OrderBy(k => k))
            {
                var rp = args.GetObject<ReadFileParams>(key);
                if (rp != null) fileParamsList.Add(rp);
            }
        }

        // 2. Fallback to param* format
        if (fileParamsList.Count == 0)
        {
            var paramKeys = args.Keys.Where(k => k.StartsWith("param", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k).ToList();
            if (paramKeys.Count > 0)
            {
                var startLine = args.GetInt("start_line", -1);
                var lineCount = args.GetInt("line_count", -1);

                foreach (var key in paramKeys)
                {
                    var path = args.GetString(key);
                    if (!string.IsNullOrEmpty(path) && !path.StartsWith(":"))
                    {
                        fileParamsList.Add(new ReadFileParams
                        {
                            Name = path,
                            StartLine = startLine,
                            LineCount = lineCount
                        });
                    }
                }
            }
        }

        if (fileParamsList.Count == 0)
        {
            return new VsResponse { Success = false, Error = "No files specified." };
        }

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var sb = new StringBuilder();
        var isSuccess = true;

        foreach (var rp in fileParamsList)
        {
            var absPath = GetAbsolutePath(rp.Name, solutionPath);
            if (!File.Exists(absPath))
            {
                sb.AppendLine($"File \"{rp.Name}\" doesn't exist.");
                isSuccess = false;
                continue;
            }

            try
            {
                // Title for multiple files
                if (fileParamsList.Count > 1)
                {
                    sb.AppendLine($"\"{rp.Name}\" content");
                }

                // If it's a snippet or we are reading multiple files, use line numbers
                var useLineNumbers = rp.StartLine > 0 || rp.LineCount > 0 || fileParamsList.Count > 1;

                if (!useLineNumbers)
                {
                    // Raw content for single file without range
                    sb.Append(File.ReadAllText(absPath));
                }
                else
                {
                    var lines = File.ReadLines(absPath);
                    var currentLine = 0;

                    if (rp.StartLine > 0)
                    {
                        var skipCount = Math.Max(0, rp.StartLine - 1);
                        lines = lines.Skip(skipCount);
                        currentLine = skipCount;
                    }

                    if (rp.LineCount > 0)
                    {
                        lines = lines.Take(rp.LineCount);
                    }

                    foreach (var line in lines)
                    {
                        sb.AppendLine($"{++currentLine} | {line}");
                    }
                }
            }
            catch (Exception ex)
            {
                await Logger.LogAsync($"Error reading file {rp.Name}: {ex.Message}", "ERROR");
                sb.AppendLine($"Error reading file {rp.Name}: {ex.Message}");
                isSuccess = false;
            }
        }

        return new VsResponse
        {
            Success = isSuccess,
            Payload = sb.ToString()
        };
    }

    private async Task<VsResponse> CreateNewFileAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var fileParam = args.GetString("param1");
        var filepath = GetAbsolutePath(fileParam, solutionPath);
        var contents = args.Values.Skip(1).Select(x => x.ToString());

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var directory = Path.GetDirectoryName(filepath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(filepath, contents);
            return new VsResponse
            {
                Payload = $"File {fileParam} created successfully."
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

    private async Task<VsResponse> DeleteFileAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var fileParam = args.GetString("param1");
        var filepath = GetAbsolutePath(fileParam, solutionPath);

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (File.Exists(filepath))
            {
                File.Delete(filepath);
                return new VsResponse
                {
                    Payload = $"File {fileParam} deleted successfully."
                };
            }

            return new VsResponse
            {
                Success = false,
                Error = $"File {fileParam} does not exist."
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
        var param = args.GetString("param1");
        var exe = param.Split(' ')[0];
        var command = param.Remove(0, exe.Length).TrimStart();
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
        var pattern = args.GetString("param1");
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
        var query = args.GetString("param1");
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
        var dirPath = GetAbsolutePath(args.GetString("param1"), solutionPath);
        var recursive = args.GetBool("param2");

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

    private async Task<VsResponse> ApplyDiffAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var inputFileName = args.GetString("param1");
        var filepath = GetAbsolutePath(inputFileName, solutionPath);
        var replacements = new List<DiffReplacement>();
        foreach (var item in args)
        {
            if (item.Key.StartsWith("diff") && item.Value is JsonElement jsonElement)
            {
                replacements.Add(jsonElement.GetObject<DiffReplacement>());
            }
        }

        if (replacements.Count == 0)
            return new VsResponse { Success = false, Error = "Failed to get replacements" };

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!File.Exists(filepath))
            return new VsResponse { Success = false, Error = "File doesn't exist." };

        if (replacements == null || replacements.Count == 0)
            return new VsResponse { Success = false, Error = "No valid replacements found in diff." };

        var lines = File.ReadAllLines(filepath).ToList();
        var totalReplacements = 0;
        var appliedReplacements = new List<string>();

        replacements = replacements.OrderByDescending(r => r.StartLine).ToList();

        var parser = new UniversalDiffParser();

        foreach (var rep in replacements)
        {
            var actualStart = parser.FindInFile(lines, rep.Search, rep.StartLine, 5);
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

        if (totalReplacements == 0)
        {
            return new VsResponse { Success = false, Error = "No valid replacements found." };
        }

        try
        {
            var tempFile = Path.Combine(Path.GetTempPath(), Path.GetFileName(filepath));
            File.WriteAllLines(filepath, lines, Encoding.UTF8); // TODO настройка кодировки, может кому-то нужен BOM
            await OpenEditorAndCleanUp(filepath);
            await Logger.LogAsync($"{totalReplacements} changes successfully applied to {inputFileName}.\r\nLooks good!");
        }
        catch (Exception e)
        {
            await Logger.LogAsync(e.Message);
            return new VsResponse
            {
                Payload = $"Changes to {inputFileName} is FAILED.\r\n{e.Message}"
            };
        }

        return new VsResponse
        {
            Payload = $"Changes successfully applied to {inputFileName}.\r\nLooks good!. Applied {totalReplacements}/{replacements.Count} replacements. Use read_files to get actual content."
        };
    }

    /// <summary>
    /// Открываем файл в редакторе Visual Studio
    /// </summary>
    public async Task OpenEditorAndCleanUp(string filepath)
    {
        var docView = await VS.Documents.OpenAsync(filepath);

        // TODO не работает. Надо смотреть другие варианты
        //if (docView != null)
        //{
        //    // Переключаемся на основной поток для выполнения команд IDE
        //    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        //    // 3. Запускаем Code Cleanup
        //    var dte = Shell.Package.GetGlobalService(typeof(DTE)) as DTE;

        //    // Выполняем очистку
        //    dte?.ExecuteCommand("Analyze.RunCodeCleanup");

        //    // охраняем файл после очистки
        //    docView.Document.Save();
        //}
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
        var buildAction = args.GetString("param1").ToLower() switch
        {
            "clean" => Toolkit.BuildAction.Clean,
            "rebuild" => Toolkit.BuildAction.Rebuild,
            _ => Toolkit.BuildAction.Build,
        };
        var result = await VS.Build.BuildSolutionAsync(buildAction);

        if (!result)
        {
            var errorList = await GetErrorListAsync();

            return new VsResponse
            {
                Success = false,
                Error = $"""
                         {buildAction} is failed.

                         {errorList.Error}
                         """
            };
        }

        return new VsResponse
        {
            Payload = $"{buildAction} is successful."
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

    private async Task<VsResponse> GetSolutionStructureAsync()
    {
        var sb = new StringBuilder();
        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var projects = await VS.Solutions.GetAllProjectsAsync();

        foreach (var project in projects)
        {
            sb.AppendLine($"Project: {project.Name}");
            await WalkSolutionItemsAsync(project.Children, sb, 1);
        }

        return new VsResponse
        {
            Payload = sb.ToString()
        };
    }

    private async Task WalkSolutionItemsAsync(IEnumerable<Toolkit.SolutionItem> items, StringBuilder sb, int indent)
    {
        var indentString = new string(' ', indent * 2);
        foreach (var item in items)
        {
            if (item.Type == Toolkit.SolutionItemType.PhysicalFile)
            {
                var ext = Path.GetExtension(item.FullPath).ToLower();
                if (ext is ".zip" or ".bin" or ".dll" or ".exe" or ".png" or ".jpg" or ".obj" or ".pdb") continue;

                sb.AppendLine($"{indentString}📄 {item.Name}");
            }
            else if (item.Type == Toolkit.SolutionItemType.PhysicalFolder || item.Type == Toolkit.SolutionItemType.Project)
            {
                sb.AppendLine($"{indentString}📁 {item.Name}");
                await WalkSolutionItemsAsync(item.Children, sb, indent + 1);
            }
        }
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

    private async Task<VsResponse> GetRulesAsync()
    {
        var solutionPath = await GetSolutionPathAsync();
        var localRulesPath = Path.Combine(solutionPath, ".agent", "rules.md");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var globalRulesPath = Path.Combine(appData, "Agent", "rules.md");

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!File.Exists(localRulesPath) && !File.Exists(globalRulesPath))
        {
            return new VsResponse { Success = true, Payload = string.Empty };
        }

        try
        {
            var content = new StringBuilder();

            if (File.Exists(globalRulesPath))
            {
                content.AppendLine(File.ReadAllText(globalRulesPath));
                content.AppendLine();
            }

            if (File.Exists(localRulesPath))
            {
                content.AppendLine(File.ReadAllText(localRulesPath));
            }

            return new VsResponse { Success = true, Payload = content.ToString().Trim() };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = $"Error reading rules.md: {ex.Message}" };
        }
    }

    private async Task<VsResponse> OpenFileInEditorAsync(IReadOnlyDictionary<string, object> args)
    {
        var relativePath = args.GetString("param1");
        if (string.IsNullOrEmpty(relativePath))
        {
            return new VsResponse { Success = false, Error = "File path is empty" };
        }

        var solutionPath = await GetSolutionPathAsync();

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            var normalizedPath = relativePath.Replace('\\', '/');
            if (normalizedPath.Equals(".agent/rules.md", StringComparison.OrdinalIgnoreCase))
            {
                var localPath = GetAbsolutePath(relativePath, solutionPath);
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var globalPath = Path.Combine(appData, "Agent", "rules.md");

                if (!File.Exists(localPath) && File.Exists(globalPath))
                {
                    relativePath = globalPath;
                }
            }
            else if (normalizedPath.Equals(".agent/skills/readme.md", StringComparison.OrdinalIgnoreCase))
            {
                var localPath = GetAbsolutePath(relativePath, solutionPath);
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var globalPath = Path.Combine(appData, "Agent", "skills", "README.md");

                if (!File.Exists(localPath) && File.Exists(globalPath))
                {
                    relativePath = globalPath;
                }
            }

            var absolutePath = GetAbsolutePath(relativePath, solutionPath);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(absolutePath))
            {
                File.WriteAllText(absolutePath, "");
                // Если создаем новый файл, логируем
                await Logger.LogAsync($"Created new file: {relativePath}");
            }

            var doc = await VS.Documents.OpenAsync(absolutePath);
            if (doc == null)
            {
                // fallback using DTE
                var dte = (DTE2)Shell.Package.GetGlobalService(typeof(DTE));
                dte.ItemOperations.OpenFile(absolutePath);
            }

            return new VsResponse { Success = true, Payload = $"Opened {relativePath}" };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = $"Error opening {relativePath}: {ex.Message}" };
        }
    }

    #region Skills Support
    private void OnSkillFileChanged(object sender, FileSystemEventArgs e)
    {
        _skillsCache.AddOrUpdate(e.FullPath, DateTime.UtcNow, (k, v) => DateTime.UtcNow);
        Logger.Log($"Skill file changed: {e.Name}");
        // TODO: Уведомить UIBlazor через WebView2 о необходимости обновления
    }

    private void OnSkillFileRenamed(object sender, RenamedEventArgs e)
    {
        _skillsCache.TryRemove(e.OldFullPath, out _);
        _skillsCache.AddOrUpdate(e.FullPath, DateTime.UtcNow, (k, v) => DateTime.UtcNow);
        Logger.Log($"Skill file renamed: {e.OldName} -> {e.Name}");
    }

    private IEnumerable<string> GetSkillsPaths(string solutionPath)
    {
        var yieldedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Локальные скиллы (приоритет)
        if (!string.IsNullOrEmpty(solutionPath) && Directory.Exists(solutionPath))
        {
            var localSkills = Directory.EnumerateFileSystemEntries(solutionPath, "*SKILL.md", SearchOption.AllDirectories)
                .Where(path => path.Split(Path.DirectorySeparatorChar).Contains("skills"));

            foreach (var path in localSkills)
            {
                var fileName = Path.GetFileName(path);
                if (yieldedNames.Add(fileName))
                {
                    yield return path;
                }
            }
        }

        // 2. Глобальные скиллы
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var globalSkillsDir = Path.Combine(appData, "Agent", "skills");
        if (Directory.Exists(globalSkillsDir))
        {
            var globalSkills = Directory.EnumerateFileSystemEntries(globalSkillsDir, "*SKILL.md", SearchOption.AllDirectories);
            foreach (var path in globalSkills)
            {
                var fileName = Path.GetFileName(path);
                if (yieldedNames.Add(fileName))
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>
    /// Получить метаданные всех скиллов (только название + описание для системного промпта)
    /// </summary>
    private async Task<VsResponse> GetSkillsMetadataAsync()
    {
        var solutionPath = await GetSolutionPathAsync();
        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var skillFiles = GetSkillsPaths(solutionPath);

        var metadataList = new List<Dictionary<string, string>>();

        foreach (var file in skillFiles)
        {
            try
            {
                var firstFiveLines = File.ReadLines(file).Take(5).ToList();
                var (name, description) = ParseYamlFrontmatter(firstFiveLines);
                var relativePath = MakeRelativeToSolution(file, solutionPath);

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(description))
                {
                    continue;
                }

                metadataList.Add(new Dictionary<string, string>
                {
                    { "name", name },
                    { "description", description },
                    { "filePath", relativePath }
                });
                await Logger.LogAsync($"Added skill metadata. {name} - {file}");
            }
            catch (Exception ex)
            {
                await Logger.LogAsync($"Error reading skill metadata {file}: {ex.Message}");
            }
        }

        return new VsResponse
        {
            Payload = JsonSerializer.Serialize(metadataList)
        };
    }

    /// <summary>
    /// Читать полное содержимое скилла (вызывается только при активации)
    /// </summary>
    private async Task<VsResponse> ReadSkillContentAsync(IReadOnlyDictionary<string, object> args)
    {
        var skillName = args.GetString("param1");
        var solutionPath = await GetSolutionPathAsync();
        var fullPath = GetSkillsPaths(solutionPath)
            .FirstOrDefault(path => path.Split(Path.DirectorySeparatorChar).Last().Equals(skillName, StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        if (!File.Exists(fullPath))
        {
            return new VsResponse
            {
                Success = false,
                Error = $"Skill file not found: {skillName}"
            };
        }

        try
        {
            var content = File.ReadAllText(fullPath);
            var (name, description) = ParseYamlFrontmatter(content);
            var markdownContent = StripYamlFrontmatter(content);
            var resources = ExtractResources(markdownContent);

            var skillContent = new Dictionary<string, object>
            {
                { "name", name },
                { "description", description },
                { "content", markdownContent },
                { "resources", resources }
            };

            return new VsResponse
            {
                Payload = JsonSerializer.Serialize(skillContent)
            };
        }
        catch (Exception ex)
        {
            return new VsResponse
            {
                Success = false,
                Error = $"Error reading skill content: {ex.Message}"
            };
        }
    }

    private (string name, string description) ParseYamlFrontmatter(IEnumerable<string> lines)
    {
        var inFrontmatter = false;
        var name = string.Empty;
        var description = string.Empty;

        foreach (var line in lines)
        {
            if (line.Trim() == "---")
            {
                if (!inFrontmatter)
                {
                    inFrontmatter = true;
                    continue;
                }
                else
                {
                    break;
                }
            }

            if (inFrontmatter)
            {
                if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    name = line.Substring(5).Trim();
                }
                else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    description = line.Substring(12).Trim();
                }
            }
        }

        return (name, description);
    }

    private (string name, string description) ParseYamlFrontmatter(string content)
    {
        return ParseYamlFrontmatter(content.Split('\n'));
    }

    private string StripYamlFrontmatter(string content)
    {
        var lines = content.Split('\n');
        var inFrontmatter = false;
        var resultLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.Trim() == "---")
            {
                inFrontmatter = !inFrontmatter;
                continue;
            }

            if (!inFrontmatter)
            {
                resultLines.Add(line);
            }
        }

        return string.Join("\n", resultLines).Trim();
    }

    private List<string> ExtractResources(string content)
    {
        var resources = new List<string>();
        var lines = content.Split('\n');
        var inResourcesSection = false;

        foreach (var line in lines)
        {
            if (line.Trim().StartsWith("## Resources", StringComparison.OrdinalIgnoreCase))
            {
                inResourcesSection = true;
                continue;
            }

            if (inResourcesSection && line.Trim().StartsWith("##"))
            {
                break;
            }

            if (inResourcesSection && line.Trim().StartsWith("-"))
            {
                var resource = line.Trim().TrimStart('-').Trim();
                if (!string.IsNullOrEmpty(resource))
                {
                    resources.Add(resource);
                }
            }
        }

        return resources;
    }

    #endregion

    private async Task<VsResponse> ExecGitCommandAsync(string arguments)
    {
        return await ExecAsync(new Dictionary<string, object>() { { "param1", $"git {arguments}" } });
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

        var path = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
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
        public string Message { get; set; }

        public string FileName { get; set; }

        public int Line { get; set; }
    }

    private readonly ConcurrentDictionary<string, bool> _initializedServers = new();

    private async Task<VsResponse> StopAllMcpServersAsync()
    {
        try
        {
            await _mcpProcessManager.StopAllProcessesAsync();
            _initializedServers.Clear();
            return new VsResponse { Success = true, Payload = "All MCP processes stopped." };
        }
        catch (Exception ex)
        {
            await Logger.LogAsync($"Error stopping all MCP processes: {ex.Message}", "ERROR");
            return new VsResponse { Success = false, Error = ex.Message };
        }
    }

    private static string GetMcpSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Agent", "mcp.json");
    }

    private async Task<VsResponse> ReadMcpSettingsAsync()
    {
        try
        {
            await StopAllMcpServersAsync();

            var filePath = GetMcpSettingsPath();
            if (!File.Exists(filePath))
            {
                // Return empty settings; UI will show nothing
                return new VsResponse { Payload = "{\"mcpServers\":{}}" };
            }

            var content = File.ReadAllText(filePath, Encoding.UTF8);
            return new VsResponse { Payload = content };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = $"Error reading mcp.json: {ex.Message}" };
        }
    }

    private async Task<VsResponse> WriteMcpSettingsAsync(IReadOnlyDictionary<string, object> args)
    {
        try
        {
            var content = args.GetString("param1");
            if (string.IsNullOrEmpty(content))
            {
                return new VsResponse { Success = false, Error = "Content is empty" };
            }

            var filePath = GetMcpSettingsPath();
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await StopAllMcpServersAsync();

            File.WriteAllText(filePath, content, Encoding.UTF8);
            return new VsResponse { Payload = "OK" };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = $"Error writing mcp.json: {ex.Message}" };
        }
    }

    private async Task<VsResponse> OpenMcpSettingsAsync()
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var filePath = GetMcpSettingsPath();
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(filePath))
            {
                // Create a sample mcp.json
                var sample = "{\n  \"mcpServers\": {}\n}";
                File.WriteAllText(filePath, sample, Encoding.UTF8);
            }

            var doc = await VS.Documents.OpenAsync(filePath);
            if (doc == null)
            {
                // fallback using DTE
                var dte = (DTE2)Shell.Package.GetGlobalService(typeof(DTE));
                dte.ItemOperations.OpenFile(filePath);
            }

            return new VsResponse { Payload = $"Opened {filePath}" };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = $"Error opening mcp.json: {ex.Message}" };
        }
    }

    private async Task<string> EnsureServerRunningAsync(string serverId, string? command = null, string? arguments = null, Dictionary<string, string> env = null)
    {
        if (!_mcpProcessManager.IsProcessRunning(serverId))
        {
            if (string.IsNullOrEmpty(command)) return $"ERROR: Server {serverId} not running and no command provided to start it";

            var solutionPath = await GetSolutionPathAsync();
            var startResult = await _mcpProcessManager.StartProcessAsync(serverId, command, arguments ?? "", solutionPath, env);
            if (!startResult.StartsWith("OK")) return startResult;
        }

        if (_initializedServers.ContainsKey(serverId)) return "OK";

        var requestId = Guid.NewGuid().ToString("N");
        var initRequest = new McpRequest
        {
            Id = requestId,
            Method = "initialize",
            Params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "InvAit", version = "1.0.0" }
            }
        };

        var responseJson = await _mcpProcessManager.CallMethodAsync(serverId, requestId, JsonUtils.SerializeCompact(initRequest));
        if (responseJson.StartsWith("ERROR")) return responseJson;

        var initializedNotification = new McpNotification
        {
            Method = "notifications/initialized",
            Params = new { }
        };

        await _mcpProcessManager.SendMessageAsync(serverId, JsonUtils.SerializeCompact(initializedNotification));
        _initializedServers[serverId] = true;
        return "OK";
    }

    private async Task<VsResponse> McpGetToolsAsync(IReadOnlyDictionary<string, object> args)
    {
        var serverId = args.GetString("param1") ?? args.GetString("serverId");
        var command = args.GetString("param2") ?? args.GetString("command");
        var arguments = args.GetString("param3") ?? args.GetString("args");
        var env = args.GetDictionary("env");

        if (string.IsNullOrEmpty(serverId))
        {
            return new VsResponse { Success = false, Error = "serverId is required" };
        }

        var runResult = await EnsureServerRunningAsync(serverId, command, arguments, env);
        if (runResult != "OK")
        {
            return new VsResponse { Success = false, Error = runResult };
        }

        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var request = new McpRequest { Id = requestId, Method = "tools/list", Params = new { } };
            var responseJson = await _mcpProcessManager.CallMethodAsync(serverId, requestId, JsonUtils.SerializeCompact(request));

            return new VsResponse
            {
                Success = !responseJson.StartsWith("ERROR"),
                Payload = responseJson,
                Error = responseJson.StartsWith("ERROR") ? responseJson : null
            };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = ex.Message };
        }
    }

    private async Task<VsResponse> McpCallToolAsync(IReadOnlyDictionary<string, object> args)
    {
        var serverId = args.GetString("param1") ?? args.GetString("serverId");
        var toolName = args.GetString("param2") ?? args.GetString("toolName");
        var toolArgsRaw = args.GetValue("arguments");
        object? toolArgs = null;

        if (toolArgsRaw is string jsonStr && !string.IsNullOrEmpty(jsonStr))
        {
            toolArgs = JsonSerializer.Deserialize<object>(jsonStr);
        }
        else if (toolArgsRaw != null)
        {
            toolArgs = toolArgsRaw;
        }

        // Command/Arguments for auto-start if needed
        var command = args.GetString("command");
        var commandArgs = args.GetString("args");
        var env = args.GetDictionary("env");

        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(toolName))
        {
            return new VsResponse { Success = false, Error = "serverId and toolName are required" };
        }

        var runResult = await EnsureServerRunningAsync(serverId, command, commandArgs, env);
        if (runResult != "OK")
        {
            return new VsResponse { Success = false, Error = runResult };
        }

        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var request = new McpRequest
            {
                Id = requestId,
                Method = "tools/call",
                Params = new { name = toolName, arguments = toolArgs ?? new { } }
            };

            var responseJson = await _mcpProcessManager.CallMethodAsync(serverId, requestId, JsonUtils.SerializeCompact(request));
            return new VsResponse
            {
                Success = !responseJson.StartsWith("ERROR"),
                Payload = responseJson,
                Error = responseJson.StartsWith("ERROR") ? responseJson : null
            };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = ex.Message };
        }
    }
}
