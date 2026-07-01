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
using Microsoft.Build.Evaluation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Shared.Contracts;
using ToolCore;
using IAsyncDisposable = Microsoft.VisualStudio.Threading.IAsyncDisposable;
using Process = System.Diagnostics.Process;
using Shell = Microsoft.VisualStudio.Shell;
using Toolkit = Community.VisualStudio.Toolkit;
using VS = Community.VisualStudio.Toolkit.VS;

namespace InvAit.Agent;

public class ToolExecutor : IAsyncDisposable
{
    private readonly McpProcessManager _mcpProcessManager = new(new VsLogger());
    // private readonly McpManager _mcpManager = new(new VsLogger());
    private readonly ProcessExecutor _processExecutor = new(new VsLogger());
    private readonly Dictionary<string, string> _skillPathByName = [];
    private readonly FileUtils _fileUtils = new();

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
                BuiltInToolEnum.Bash => await ExecAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.SearchFiles => await SearchFilesAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Grep => await GrepSearchAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.FindDeclarations => await FindDeclarationsAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.FindReferences => await FindReferencesAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Dir => await ListDirectoryAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.ApplyDiff => await ApplyDiffAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Build => await BuildSolutionAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.RunTests => await RunTestsAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GetErrors => await GetErrorListAsync(),
                BuiltInToolEnum.GetProjectInfo => await GetProjectInfoAsync(),
                BuiltInToolEnum.GetSolutionStructure => await GetSolutionStructureAsync(),
                BuiltInToolEnum.GitStatus => await GitStatusAsync(),
                BuiltInToolEnum.GitLog => await GitLogAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GitDiff => await GitDiffAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.OpenFile => await OpenFileInEditorAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.OpenFolder => await OpenFolderInExplorerAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.GetSkillsMetadata => await GetSkillsMetadataAsync(),
                BasicEnum.ReadSkillContent => await ReadSkillContentAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.GetRules => await GetRulesAsync(),
                BasicEnum.GetAgents => await ReadFileBaseAsync(new List<ReadFileParams> { { new ReadFileParams { Name = "agents.md" } } }, onlyContent: true),
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

        return await ReadFileBaseAsync(new List<ReadFileParams>
        {
            { new ReadFileParams { Name = docView.Document.FilePath } }
        });
    }

    private async Task<VsResponse> ReadFileAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var fileParamsList = new List<ReadFileParams>();

        // Try to get structured parameters (file1, file2, ...)
        var fileKeys = args.Keys.Where(k => k.StartsWith("file", StringComparison.OrdinalIgnoreCase)).ToList();
        if (fileKeys.Count > 0)
        {
            foreach (var key in fileKeys.OrderBy(k => k))
            {
                var rp = args.GetObject<ReadFileParams>(key);
                if (rp != null) fileParamsList.Add(rp);
            }
        }

        return await ReadFileBaseAsync(fileParamsList);
    }

    private async Task<VsResponse> ReadFileBaseAsync(List<ReadFileParams> fileParamsList, bool onlyContent = false)
    {
        var solutionPath = await GetSolutionPathAsync();

        if (fileParamsList.Count == 0)
        {
            return new VsResponse { Success = false, Error = "No files specified." };
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var sb = new StringBuilder();
        var isSuccess = true;

        for (var i = 0; i < fileParamsList.Count; i++)
        {
            var rp = fileParamsList[i];
            var absPath = GetAbsolutePath(rp.Name, solutionPath);

            if (!File.Exists(absPath))
            {
                sb.AppendLine($"File \"{rp.Name}\" doesn't exist.");
                isSuccess = false;
                continue;
            }

            if (i > 0)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }

            try
            {
                if (!onlyContent)
                {
                    sb.AppendLine($"### {rp.Name}");
                    sb.AppendLine("```");
                }
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

                if (!onlyContent)
                {
                    foreach (var line in lines)
                    {
                        sb.AppendLine($"{++currentLine} | {line}");
                    }
                    sb.AppendLine("```");
                }
                else
                {
                    foreach (var line in lines)
                    {
                        sb.AppendLine(line);
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
            Payload = isSuccess ? sb.ToString() : null,
            Error = isSuccess ? null : sb.ToString()
        };
    }

    private async Task<VsResponse> CreateNewFileAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var fileParam = args.GetString("param1");
        var filepath = GetAbsolutePath(fileParam, solutionPath);
        var contents = args.Values.Skip(1).Select(x => x.ToString());

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var directory = Path.GetDirectoryName(filepath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(filepath, contents, Encoding.UTF8);
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

        var solutionPath = await GetSolutionPathAsync();

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var result = await _processExecutor.ExecuteBashAsync(param, solutionPath, 120_000);
        return new VsResponse
        {
            Success = result.Success,
            Payload = result.Success ? $"Command executed successfully: {result.Output}" : $"Error: {result.Error}",
            Error = result.Error
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
                Error = "Regex pattern should be not empty."
            };
        }

        var solutionPath = await GetSolutionPathAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var regex = new Regex(pattern, RegexOptions.Compiled);
        var matchedFiles = new List<SearchFileInfo>();

        foreach (var file in Directory.EnumerateFiles(solutionPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = MakeRelativeToSolution(file, solutionPath);
            if (!regex.IsMatch(relativePath)) continue;

            var fileInfo = new FileInfo(file);
            matchedFiles.Add(new SearchFileInfo
            {
                Path = relativePath,
                Extension = fileInfo.Extension.ToLower(),
                SizeKB = fileInfo.Length / 1024.0
            });
        }

        if (matchedFiles.Count == 0)
        {
            return new VsResponse { Payload = "No files found matching pattern." };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {matchedFiles.Count} files matching pattern:");
        sb.AppendLine();

        foreach (var f in matchedFiles)
        {
            var icon = f.Extension switch
            {
                ".csproj" => "📦",
                ".sln" => "📦",
                ".slnx" => "📦",
                _ => "📄"
            };
            sb.AppendLine($"{icon} {f.Path} [{f.SizeKB:F1} KB]");
        }

        return new VsResponse { Payload = sb.ToString() };
    }

    private async Task<VsResponse> GrepSearchAsync(IReadOnlyDictionary<string, object> args)
    {
        var query = args.GetString("param1");
        var contextLines = args.GetInt("contextLines", 3);
        var maxMatches = args.GetInt("maxMatches", 50);

        if (string.IsNullOrEmpty(query))
        {
            return new VsResponse
            {
                Success = false,
                Error = "Parameter 'query' is required."
            };
        }

        var solutionPath = await GetSolutionPathAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var files = await GetAllSolutionFilesAsync();
        var regex = new Regex(query, RegexOptions.Multiline);
        var sb = new StringBuilder();
        var totalMatches = 0;

        foreach (var file in files)
        {
            if (totalMatches >= maxMatches) break;

            try
            {
                var lines = File.ReadAllLines(file);
                var fileMatches = new List<int>();

                for (var i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        fileMatches.Add(i);
                        totalMatches++;
                        if (totalMatches >= maxMatches) break;
                    }
                }

                if (fileMatches.Count == 0) continue;

                var relativePath = MakeRelativeToSolution(file, solutionPath);
                sb.AppendLine($"### {relativePath}");
                sb.AppendLine($"Matches: {fileMatches.Count}");
                sb.AppendLine();

                foreach (var lineIdx in fileMatches)
                {
                    var lineNum = lineIdx + 1;
                    sb.AppendLine($"Match at line {lineNum}:");

                    var start = Math.Max(0, lineIdx - contextLines);
                    var end = Math.Min(lines.Length - 1, lineIdx + contextLines);

                    for (var i = start; i <= end; i++)
                    {
                        var marker = i == lineIdx ? ">" : " ";
                        var num = (i + 1).ToString().PadLeft(4);
                        sb.AppendLine($"{marker} {num} | {lines[i]}");
                    }

                    sb.AppendLine("---");
                }

                sb.AppendLine();
            }
            catch
            {
                // Skip files that can't be read
            }
        }

        if (totalMatches == 0)
        {
            return new VsResponse { Payload = "No matches found." };
        }

        var header = totalMatches >= maxMatches
            ? $"Found {totalMatches}+ matches (limited to {maxMatches}):\n\n"
            : $"Found {totalMatches} matches:\n\n";

        return new VsResponse { Payload = header + sb.ToString() };
    }

    private async Task<VsResponse> FindDeclarationsAsync(IReadOnlyDictionary<string, object> args)
    {
        var symbolName = args.GetString("param1");
        var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        var workspace = componentModel.GetService<VisualStudioWorkspace>();
        var solution = workspace.CurrentSolution;

        // Находим декларации символов по текстовому имени во всем решении
        var symbols = (await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
            solution,
            symbolName,
            SymbolFilter.Type | SymbolFilter.Member)).ToList();

        if (symbols.Count == 0)
        {
            return new VsResponse { Success = false, Error = $"Symbol '{symbolName}' is`t found." };
        }

        var result = symbols.Select(s => $"{s.Name} | {s.Kind} | {s.Locations.FirstOrDefault()?.SourceTree?.FilePath}").ToList();

        return new VsResponse
        {
            Payload = string.Join("\n", result)
        };
    }

    private async Task<VsResponse> FindReferencesAsync(IReadOnlyDictionary<string, object> args)
    {
        var symbolName = args.GetString("param1");

        var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        var workspace = componentModel.GetService<VisualStudioWorkspace>();
        var solution = workspace.CurrentSolution;

        // Находим декларации символов по текстовому имени во всем решении
        var symbols = (await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
            solution,
            symbolName,
            SymbolFilter.Type | SymbolFilter.Member)).ToList();

        if (symbols.Count == 0)
        {
            return new VsResponse { Success = false, Error = $"Symbol '{symbolName}' is`t found." };
        }

        var sb = new StringBuilder();

        // Для каждого найденного символа (на случай, если имя дублируется в разных классах)
        foreach (var symbol in symbols)
        {
            // Вызываем поиск зависимостей
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution);
            var refs = new Dictionary<string, HashSet<int>>();
            foreach (var reference in references)
            {
                foreach (var location in reference.Locations)
                {
                    // Защита от ссылок вне физических документов проекта
                    if (location.Document == null)
                        continue;

                    if (!refs.TryGetValue(location.Document.FilePath, out var lines))
                    {
                        refs[location.Document.FilePath] = lines = [];
                    }

                    var linePos = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                    lines.Add(linePos);
                }
            }

            if (refs.Count == 0)
            {
                continue;
            }

            sb.AppendLine($"--- References for: {symbol.ToDisplayString()} ---");
            foreach (var r in refs)
            {
                var sortedLines = r.Value.OrderBy(n => n);
                sb.AppendLine($"Used in: {r.Key}, line: {string.Join(", ", sortedLines)}");
            }
        }

        return new VsResponse { Payload = sb.ToString() };
    }

    private async Task<VsResponse> ListDirectoryAsync(IReadOnlyDictionary<string, object> args)
    {
        var solutionPath = await GetSolutionPathAsync();
        var dirPath = GetAbsolutePath(args.GetString("param1"), solutionPath);
        var recursive = args.GetBool("param2");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
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

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!File.Exists(filepath))
            return new VsResponse { Success = false, Error = "File doesn't exist." };

        var fileData = await _fileUtils.ReadFileWithMetadataAsync(filepath);
        var lines = fileData.Lines;
        var totalReplacements = 0;
        var appliedReplacements = new List<string>();

        replacements = [.. replacements.OrderByDescending(r => r.StartLine)];

        foreach (var rep in replacements)
        {
            var actualStart = UniversalDiffParser.FindInFile(lines, rep.Search, rep.StartLine, 5);
            if (actualStart == -1)
            {
                appliedReplacements.Add($"FAILED at line {rep.StartLine}");
                continue;
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
            await _fileUtils.SaveFileAsync(filepath, lines, fileData.Encoding, fileData.Separator, fileData.HasFinalNewLine);
            await OpenEditorAsync(filepath);
            await Logger.LogAsync($"{totalReplacements} changes successfully applied to {inputFileName}.");
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
            Payload = $"Changes successfully applied to {inputFileName}.\nApplied {totalReplacements}/{replacements.Count} replacements."
        };
    }

    /// <summary>
    /// Открываем файл в редакторе Visual Studio
    /// </summary>
    public async Task OpenEditorAsync(string filepath)
    {
        await VS.Documents.OpenAsync(filepath);
    }

    public int FindSubarrayIndex(List<string> bigArray, List<string> smallArray)
    {
        if (bigArray == null || smallArray == null || bigArray.Count < smallArray.Count)
        {
            return -1;
        }

        for (var i = 0; i <= bigArray.Count - smallArray.Count; i++)
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
        var buildAction = args.GetString("param1")?.ToLower() switch
        {
            "clean" => Toolkit.BuildAction.Clean,
            "rebuild" => Toolkit.BuildAction.Rebuild,
            _ => Toolkit.BuildAction.Build,
        };
        var result = await VS.Build.BuildSolutionAsync(buildAction);

        if (!result)
        {
            await Task.Delay(2000); // ошибки не сразу появляются
            var errorList = await GetBuildErrorListAsync();
            return new VsResponse
            {
                Success = false,
                Error = $"""
                         {buildAction} is failed.
                         ---
                         Errors:
                         {errorList}
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
        var errorList = await GetBuildErrorListAsync();
        return new VsResponse
        {
            Payload = string.IsNullOrEmpty(errorList) ? "No errors" : errorList
        };
    }

    private async Task<string> GetBuildErrorListAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        var errorList = dte?.ToolWindows?.ErrorList?.ErrorItems;
        if (errorList == null)
        {
            return string.Empty;
        }

        var errors = new List<BuildError>();

        for (var i = 1; i <= errorList.Count; i++)
        {
            var errorItem = errorList.Item(i);
            try
            {
                if (errorItem.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh) // только ошибки
                {
                    errors.Add(new BuildError
                    {
                        Message = errorItem.Description,
                        FileName = errorItem.FileName,
                        Line = errorItem.Line
                    });
                }
            }
            catch
            {
                // safe skip error
            }
        }

        return string.Join("\n", errors.Select(e => $" - {e.FileName} | line:{e.Line}\n{e.Message}\n"));
    }

    private async Task<(List<string> Dlls, List<string> Exes)> GetTestAssembliesAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var projects = await VS.Solutions.GetAllProjectsAsync();
        var dllPaths = new List<string>();
        var exePaths = new List<string>();

        foreach (var project in projects)
        {
            // 1. Проверяем, является ли проект тестовым (по названию или по наличию свойств)
            if (!project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                continue;

            var msbuildProject = ProjectCollection.GlobalProjectCollection.GetLoadedProjects(project.FullPath).FirstOrDefault()
                             ?? new Microsoft.Build.Evaluation.Project(project.FullPath);

            var fullDllPath = msbuildProject.GetPropertyValue("TargetPath");

            if (File.Exists(fullDllPath))
            {
                dllPaths.Add(fullDllPath);
            }

            var exe = Path.ChangeExtension(fullDllPath, "exe");
            if (File.Exists(exe))
            {
                exePaths.Add(exe);
            }
        }
        return (dllPaths, exePaths);
    }

    /// <summary>
    /// Запуск тестов через консоль
    /// Через TestExplorer можно запустить, но не узнать о завершении и прочитать ошибки тоже нельзя
    /// </summary>
    /// <returns></returns>
    private async Task<VsResponse> RunTestsAsync(IReadOnlyDictionary<string, object> args)
    {
        // Для начала запуск билда
        var build = await BuildSolutionAsync(new Dictionary<string, object>() { { "param1", "build" } });
        if (!build.Success)
        {
            return build;
        }

        var solutionPath = await GetSolutionPathAsync();
        var (testDlls, testExe) = await GetTestAssembliesAsync();
        var allDlls = string.Join(" ", testDlls.Select(d => $"\"{d}\""));
        var addArgs = string.Join(" ", args.Select(a => a.Value));

        

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test {allDlls} -v d {addArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = solutionPath
        };

        // xUnit - запуск исполняемых файлов вместо dotnet test
        if (testExe.Count > 0)
        {
            startInfo.FileName = testExe[0];
            startInfo.Arguments = addArgs;
            Logger.Log($"Run xUnit exe {testExe[0]} {addArgs}");
        }
        else
        {
            Logger.Log($"Run dotnet test for {allDlls} {addArgs}");
        }

        startInfo.EnvironmentVariables["TERM"] = "dumb"; // терминал тупой - отключает интерактивность
        startInfo.EnvironmentVariables["NO_COLOR"] = "1";

        using var process = Process.Start(startInfo);
        if (process == null)
            return new VsResponse
            {
                Success = false,
                Error = "Failed to start process"
            };

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = await outputTask;
        var stderr = await errorTask;

        // Теперь можно один раз считать ошибки
        if (process.ExitCode != 0)
        {
            return new VsResponse
            {
                Success = false,
                Error = $"{stdout}\n\n{stderr}"
            };
        }
        else
        {
            return new VsResponse
            {
                Payload = stdout
            };
        }
    }

    private async Task<VsResponse> GetSolutionStructureAsync()
    {
        return new VsResponse
        {
            Payload = string.Join("\n", await SolutionStructure.BuildStructureAsync(fullPath: false))
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
        return await ExecAsync(new Dictionary<string, object>()
        {
            ["param1"] = "git status"
        });
    }

    private async Task<VsResponse> GitLogAsync(IReadOnlyDictionary<string, object> args)
    {
        var limit = args.GetString("param1") ?? "20";
        return await ExecAsync(new Dictionary<string, object>()
        {
            ["param1"] = $"git log -n {limit} --pretty=format:\"%h - %s | %ad\" --stat --date=short"
        });
    }

    private async Task<VsResponse> GitDiffAsync(IReadOnlyDictionary<string, object> args)
    {
        var revisions = args.GetString("revisions");
        return await ExecAsync(new Dictionary<string, object>()
        {
            ["param1"] = $"git diff {revisions}"
        });
    }

    private async Task<VsResponse> GetRulesAsync()
    {
        var solutionPath = await GetSolutionPathAsync();
        var localRulesPath = Path.Combine(solutionPath, ".agents", "rules.md");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalRulesPath = Path.Combine(appData, ".agents", "rules.md");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var hasGlobal = File.Exists(globalRulesPath);
        var hasLocal = File.Exists(localRulesPath);

        if (!hasGlobal && !hasLocal)
        {
            return new VsResponse { Success = true, Payload = string.Empty };
        }

        try
        {
            var content = new StringBuilder();

            if (hasGlobal)
            {
                content.AppendLine("## Global rules");
                content.AppendLine(File.ReadAllText(globalRulesPath));
            }

            if (hasLocal)
            {
                var priorityNote = string.Empty;
                if (hasGlobal)
                {
                    content.AppendLine();
                    priorityNote = "(higher priority) ";
                }
                content.AppendLine($"## Local rules {priorityNote}of this project");
                content.AppendLine(File.ReadAllText(localRulesPath));
            }

            return new VsResponse { Success = true, Payload = content.ToString() };
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

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            var normalizedPath = relativePath.Replace('\\', '/');
            if (normalizedPath.Equals(".agents/rules.md", StringComparison.OrdinalIgnoreCase))
            {
                var localPath = GetAbsolutePath(relativePath, solutionPath);
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var globalPath = Path.Combine(userProfile, ".agents", "rules.md");

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

    private async Task<VsResponse> OpenFolderInExplorerAsync(IReadOnlyDictionary<string, object> args)
    {
        var relativePath = args.GetString("param1");
        var solutionPath = await GetSolutionPathAsync();
        var folderPath = string.IsNullOrEmpty(relativePath) ? solutionPath : GetAbsolutePath(relativePath, solutionPath);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            Directory.CreateDirectory(folderPath);
            if (Directory.Exists(folderPath))
            {
                Process.Start("explorer.exe", folderPath);
                return new VsResponse { Success = true, Payload = $"Folder {folderPath} opened in Explorer" };
            }
            else if (File.Exists(folderPath))
            {
                // If it's a file, open the containing folder and select the file
                Process.Start("explorer.exe", $"/select,\"{folderPath}\"");
                return new VsResponse { Success = true, Payload = $"Folder containing {folderPath} opened in Explorer" };
            }
            else
            {
                // Try to create directory if it doesn't exist? No, better just return error for folder opening
                return new VsResponse { Success = false, Error = $"Path {folderPath} does not exist" };
            }
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = $"Error opening folder: {ex.Message}" };
        }
    }

    #region Skills Support
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
                var fileName = Path.GetFileName(Path.GetDirectoryName(path)); // имя последней папки
                if (yieldedNames.Add(fileName))
                {
                    yield return path;
                }
            }
        }

        // 2. Глобальные скиллы
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalSkillsDir = Path.Combine(appData, ".agents", "skills");
        if (Directory.Exists(globalSkillsDir))
        {
            var globalSkills = Directory.EnumerateFileSystemEntries(globalSkillsDir, "*SKILL.md", SearchOption.AllDirectories);
            foreach (var path in globalSkills)
            {
                var fileName = Path.GetFileName(Path.GetDirectoryName(path)); // имя последней папки
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
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var skillFiles = GetSkillsPaths(solutionPath);

        var metadataList = new List<Dictionary<string, string>>();
        _skillPathByName.Clear();
        foreach (var file in skillFiles)
        {
            try
            {
                var lines = new List<string>();

                using (var reader = new StreamReader(file))
                {
                    string line;
                    // Читаем, пока не наберем 10 строк или не кончится файл
                    while (lines.Count < 10 && (line = await reader.ReadLineAsync()) != null)
                    {
                        // Если встретили разделитель — прекращаем чтение
                        if (line == "---" && lines.Count > 0)
                            break;

                        lines.Add(line);
                    }
                }

                var (name, description, _) = ParseYamlFrontmatter(lines);

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(description))
                {
                    continue;
                }

                metadataList.Add(new Dictionary<string, string>
                {
                    { "name", name },
                    { "description", description }
                });
                _skillPathByName.Add(name, file);
                await Logger.LogAsync($"Added skill metadata. {name} - {file}");
            }
            catch (Exception ex)
            {
                await Logger.LogAsync($"Error reading skill metadata {file}: {ex.Message}");
            }
        }

        return new VsResponse
        {
            Payload = JsonUtils.Serialize(metadataList)
        };
    }

    /// <summary>
    /// Читать полное содержимое скилла (вызывается только при активации)
    /// </summary>
    private async Task<VsResponse> ReadSkillContentAsync(IReadOnlyDictionary<string, object> args)
    {
        var skillName = args.GetString("param1");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!_skillPathByName.TryGetValue(skillName, out var fullPath))
        {
            return new VsResponse
            {
                Success = false,
                Error = $"Skill file not found: {skillName}"
            };
        }

        if (!File.Exists(fullPath))
        {
            return new VsResponse
            {
                Success = false,
                Error = $"Skill file not exits: {skillName}"
            };
        }

        try
        {
            var contentLines = File.ReadAllLines(fullPath, Encoding.UTF8);
            var (name, description, headerLines) = ParseYamlFrontmatter(contentLines);
            var markdownContent = string.Join("\n", contentLines.Skip(headerLines));

            var skillContent = new SkillContent
            {
                Name = name,
                Description = description,
                Content = markdownContent
            };

            return new VsResponse
            {
                Payload = JsonUtils.Serialize(skillContent)
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

    private (string name, string description, int headerLines) ParseYamlFrontmatter(IEnumerable<string> lines)
    {
        var inFrontmatter = false;
        var name = string.Empty;
        var description = string.Empty;
        var headerLines = 0;

        foreach (var line in lines)
        {
            headerLines++;

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
                else if (!string.IsNullOrEmpty(description))
                {
                    description += line.Trim(); // некоторые пишут в несколько строк
                }
            }
        }

        return (name, description, headerLines);
    }

    #endregion

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
        List<string> skipEx = [".zip", ".bin", ".dll", ".exe"];
        var projects = await VS.Solutions.GetAllProjectsAsync();
        foreach (var project in projects)
        {
            await WalkItemsAsync(project.Children, files, skipEx);
        }

        return files;
    }

    private async Task WalkItemsAsync(IEnumerable<Toolkit.SolutionItem> items, List<string> files, List<string> skipExtensions)
    {
        foreach (var item in items)
        {
            switch (item.Type)
            {
                case Toolkit.SolutionItemType.PhysicalFile:
                    if (!skipExtensions.Contains((item as Toolkit.PhysicalFile).Extension))
                        files.Add(item.FullPath);
                    break;
                case Toolkit.SolutionItemType.Project:
                    files.Add(item.FullPath);
                    await WalkItemsAsync(item.Children, files, skipExtensions);
                    break;
                case Toolkit.SolutionItemType.PhysicalFolder or Toolkit.SolutionItemType.SolutionFolder:
                    await WalkItemsAsync(item.Children, files, skipExtensions);
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
            // await _mcpManager.StopAllAsync();
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
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(appData, ".agents", "mcp.json");
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
            if (string.IsNullOrEmpty(command))
            {
                return $"ERROR: Server {serverId} not running and no command provided to start it";
            }

            _initializedServers.TryRemove(serverId, out _);
            var solutionPath = await GetSolutionPathAsync();
            var startResult = await _mcpProcessManager.StartProcessAsync(serverId, command, arguments ?? "", solutionPath, env);

            if (!startResult.StartsWith("OK"))
            {
                return startResult;
            }
        }
        else if (_initializedServers.ContainsKey(serverId))
        {
            return "OK";
        }

        // Этап инициализации (рупокожатия)
        var requestId = Guid.NewGuid().ToString("N");
        var initRequest = new McpRequest
        {
            Id = requestId,
            Method = "initialize",
            Params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "InvAit", version = Vsix.Version }
            }
        };

        var responseJson = await _mcpProcessManager.CallMethodAsync(serverId, requestId, JsonUtils.SerializeCompact(initRequest), 100_000);
        if (!responseJson.Success)
            return responseJson.Error;

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

        //try
        //{
        //    var result = await _mcpManager.ListToolsAsync(serverId);
        //    return new VsResponse { Payload = JsonUtils.Serialize(result) };
        //}
        //catch (Exception ex)
        //{
        //    return new VsResponse { Success = false, Error = ex.Message };
        //}

        // old
        var runResult = await EnsureServerRunningAsync(serverId, command, arguments, env);
        if (runResult != "OK")
        {
            return new VsResponse { Success = false, Error = runResult };
        }

        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var request = new McpRequest { Id = requestId, Method = "tools/list", Params = new { } };
            var result = await _mcpProcessManager.CallMethodAsync(serverId, requestId, JsonUtils.SerializeCompact(request), 20_000);
            return new VsResponse { Success = result.Success, Payload = result.Payload, Error = result.Error };
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

        //try
        //{
        //    var result = await _mcpManager.CallToolAsync(serverId, toolName, toolArgs);
        //    return new VsResponse { Success = result.Success, Payload = result.Payload, Error = result.Error };
        //}
        //catch (Exception ex)
        //{
        //    return new VsResponse { Success = false, Error = ex.Message };
        //}

        // old
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

            var result = await _mcpProcessManager.CallMethodAsync(serverId, requestId, JsonUtils.SerializeCompact(request), 600_000);
            return new VsResponse { Success = result.Success, Payload = result.Payload, Error = result.Error };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Error = ex.Message };
        }
    }

    public async Task DisposeAsync()
    {
        await StopAllMcpServersAsync();
    }

    private class SearchFileInfo
    {
        public string Path { get; set; }
        public string Extension { get; set; }
        public double SizeKB { get; set; }
    }
}
