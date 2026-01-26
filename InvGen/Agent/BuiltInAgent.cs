using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using InvGen.Utils;
using Shared.Contracts;
using Shared.Contracts.Mcp;
using Process = System.Diagnostics.Process;
using Shell = Microsoft.VisualStudio.Shell;
using Toolkit = Community.VisualStudio.Toolkit;
using VS = Community.VisualStudio.Toolkit.VS;

namespace InvGen.Agent;

public class BuiltInAgent
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
                BuiltInToolEnum.Exec => await ExecAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.SearchFiles => await SearchFilesAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GrepSearch => await GrepSearchAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Ls => await ListDirectoryAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.FetchUrl => await FetchUrlContentAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.ApplyDiff => await ApplyDiffAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Build => await BuildSolutionAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GetErrors => await GetErrorListAsync(),
                BuiltInToolEnum.GetProjectInfo => await GetProjectInfoAsync(),
                BuiltInToolEnum.GetSolutionStructure => await GetSolutionStructureAsync(),
                BuiltInToolEnum.GitStatus => await GitStatusAsync(),
                BuiltInToolEnum.GitLog => await GitLogAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GitDiff => await GitDiffAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GitBranch => await GitBranchAsync(),
                BuiltInToolEnum.SwitchMode => new VsResponse { Success = true, Payload = "Mode switched" },
                BuiltInToolEnum.GetSkillsMetadata => await GetSkillsMetadataAsync(),
                BuiltInToolEnum.ReadSkillContent => await ReadSkillContentAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.McpStartProcess => await McpStartProcessAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.McpStopProcess => await McpStopProcessAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.McpSendMessage => await McpSendMessageAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.McpReadMessage => await McpReadMessageAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.McpGetTools => await McpGetToolsAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
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
            { "param1", docView.Document.FilePath }
        });
    }

    private async Task<VsResponse> ReadFileAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var files = args.Values.TakeWhile(x => !x.ToString().StartsWith(":")).Select(x => x.ToString()).ToList();
        
        // Support for range-based reading if only one file is requested
        int? startLine = args.ContainsKey("start_line") ? int.Parse(args.GetString("start_line")) : null;
        int? lineCount = args.ContainsKey("line_count") ? int.Parse(args.GetString("line_count")) : null;

        await Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var stingBuilder = new StringBuilder();
        var isSuccess = true;
        foreach (var fileName in files)
        {
            var filepath = GetAbsolutePath(fileName, solutionPath);
            if (!File.Exists(filepath))
            {
                stingBuilder.AppendLine($"File \"{fileName}\" doesn't exist.");
                isSuccess = false;
                continue;
            }

            stingBuilder.AppendLine($"\"{fileName}\" content");

            try
            {
                var allLines = File.ReadLines(filepath);
                var lineNum = 0;
                
                if (startLine.HasValue)
                {
                    var linesToSkip = Math.Max(0, startLine.Value - 1);
                    allLines = allLines.Skip(linesToSkip);
                    lineNum = linesToSkip;
                }

                if (lineCount.HasValue)
                {
                    allLines = allLines.Take(lineCount.Value);
                }

                foreach (var readLine in allLines)
                {
                    stingBuilder.AppendLine($"{++lineNum} | {readLine}");
                }
            }
            catch (Exception e)
            {
                Logger.Log(e.Message);
                stingBuilder.AppendLine($"Error reading {fileName}: {e.Message}");
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

    private async Task<VsResponse> FetchUrlContentAsync(IReadOnlyDictionary<string, object> args)
    {
        var url = args.GetString("param1");
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
            File.Copy(filepath, tempFile, true);
            File.WriteAllLines(filepath, lines);
            await FileDiffAsync(tempFile, filepath, "old", "new");
            Logger.Log($"{totalReplacements} changes successfully applied to {inputFileName}.\r\nLooks good!");
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
        // на .Net Framework EnumerateFileSystemEntries не чувствителен к регистру, так что будут все скиллы
        return Directory.EnumerateFileSystemEntries(solutionPath, "*SKILL.md", SearchOption.AllDirectories)
            .Where(path => path.Split(Path.DirectorySeparatorChar).Contains("skills"));
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
                Logger.Log($"Added skill metadata. {name} - {file}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Error reading skill metadata {file}: {ex.Message}");
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
        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("file_name")]
        public string FileName { get; set; }

        [JsonPropertyName("line")]
        public int Line { get; set; }
    }

    #region MCP Process Management

    private async Task<VsResponse> McpStartProcessAsync(IReadOnlyDictionary<string, object> args)
    {
        var serverId = args.GetString("param1");
        var command = args.GetString("param2");
        var arguments = args.GetString("param3");
        var result = await _mcpProcessManager.StartProcessAsync(serverId, command, arguments);
        return new VsResponse { Success = result.StartsWith("OK"), Payload = result, Error = result.StartsWith("ERROR") ? result : null };
    }

    private async Task<VsResponse> McpStopProcessAsync(IReadOnlyDictionary<string, object> args)
    {
        var serverId = args.GetString("param1");
        var result = await _mcpProcessManager.StopProcessAsync(serverId);
        return new VsResponse { Success = result.StartsWith("OK"), Payload = result, Error = result.StartsWith("ERROR") ? result : null };
    }

    private async Task<VsResponse> McpSendMessageAsync(IReadOnlyDictionary<string, object> args)
    {
        var serverId = args.GetString("param1");
        var message = args.GetString("param2");
        var result = await _mcpProcessManager.SendMessageAsync(serverId, message);
        return new VsResponse { Success = result == "OK", Payload = result, Error = result != "OK" ? result : null };
    }

    private async Task<VsResponse> McpReadMessageAsync(IReadOnlyDictionary<string, object> args)
    {
        var serverId = args.GetString("param1");
        var timeout = args.ContainsKey("param2") ? int.Parse(args.GetString("param2")) : 5000;
        var result = await _mcpProcessManager.ReadMessageAsync(serverId, timeout);
        return new VsResponse { Success = !result.StartsWith("ERROR"), Payload = result, Error = result.StartsWith("ERROR") ? result : null };
    }

    private async Task<VsResponse> McpGetToolsAsync(IReadOnlyDictionary<string, object> args)
    {
        var serverId = args.GetString("serverId");
        if (string.IsNullOrEmpty(serverId))
        {
            serverId = "temp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        var isStartedHere = false;
        if (!_mcpProcessManager.IsProcessRunning(serverId))
        {
            var command = args.GetString("command");
            var arguments = args.GetString("args");
            if (string.IsNullOrEmpty(command)) return new VsResponse { Success = false, Error = "Command is required to start MCP server" };
            
            var startResult = await _mcpProcessManager.StartProcessAsync(serverId, command, arguments);
            if (!startResult.StartsWith("OK")) return new VsResponse { Success = false, Error = startResult };
            isStartedHere = true;
        }

        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var request = new McpRequest
            {
                Id = requestId,
                Method = "tools/list",
                Params = new { }
            };

            await _mcpProcessManager.SendMessageAsync(serverId, JsonSerializer.Serialize(request));
            var responseJson = await _mcpProcessManager.ReadMessageAsync(serverId, 10000); // 10s timeout

            if (responseJson.StartsWith("ERROR"))
            {
                return new VsResponse { Success = false, Error = responseJson };
            }

            // We expect a McpResponse. Some servers might send notifications first, but ReadMessageAsync 
            // from McpProcessManager just takes the next line. This might be fragile if there's noise.
            // But for a fresh process it should be fine.
            
            return new VsResponse { Success = true, Payload = responseJson };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = ex.Message };
        }
        finally
        {
            if (isStartedHere)
            {
                await _mcpProcessManager.StopProcessAsync(serverId);
            }
        }
    }

    #endregion
}
