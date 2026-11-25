using System;
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
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Shared.Contracts;
using Shared.Contracts.McpHost;
using ToolCore;
using ToolCore.McpHost;
using Process = System.Diagnostics.Process;
using Shell = Microsoft.VisualStudio.Shell;
using Toolkit = Community.VisualStudio.Toolkit;
using VS = Community.VisualStudio.Toolkit.VS;

namespace InvAit.Agent;

public class ToolExecutor
{
    private readonly ProcessExecutor _processExecutor = new(new VsLogger());
    private readonly Dictionary<string, string> _skillPathByName = [];
    private readonly FileUtils _fileUtils = new();
    private readonly FileWriter _fileWriter = new();

    public async Task<VsResponse> ExecuteAsync(VsRequest vsRequest)
    {
        try
        {
            var response = vsRequest.Action switch
            {
                BuiltInToolEnum.ReadFiles => await ReadFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload).GetObject<List<ReadFileParams>>("files")),
                BuiltInToolEnum.ReadOpenFile => await ReadCurrentlyOpenFileAsync(),
                BuiltInToolEnum.CreateFile => await CreateNewFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.DeleteFile => await DeleteFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Bash => await ExecAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.SearchFiles => await SearchFilesAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Grep => await GrepSearchAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.FindDeclarations => await FindDeclarationsAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.FindReferences => await FindReferencesAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Dir => await ListDirectoryAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.EditFile => await EditFileAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.Build => await BuildSolutionAsync(),
                BuiltInToolEnum.RunTests => await RunTestsAsync(),
                BuiltInToolEnum.GetErrors => await GetErrorListAsync(),
                BuiltInToolEnum.GetProjectInfo => await GetProjectInfoAsync(),
                BuiltInToolEnum.GetSolutionStructure => await GetSolutionStructureAsync(),
                BuiltInToolEnum.GitStatus => await GitStatusAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GitLog => await GitLogAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BuiltInToolEnum.GitDiff => await GitDiffAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.OpenFile => await OpenFileInEditorAsync(vsRequest.Payload),
                BasicEnum.OpenFolder => await OpenFolderInExplorerAsync(vsRequest.Payload),
                BasicEnum.GetSkillsMetadata => await GetSkillsMetadataAsync(),
                BasicEnum.ReadSkillContent => await ReadSkillContentAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.GetRules => await GetRulesAsync(),
                BasicEnum.GetAgents => await ReadFileAsync(new List<ReadFileParams> { { new ReadFileParams { Path = "agents.md" } } }, onlyContent: true),
                // MCP
                BasicEnum.McpGetTools => await McpGetToolsAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.McpCallTool => await McpCallToolAsync(JsonUtils.DeserializeParameters(vsRequest.Payload)),
                BasicEnum.McpStopAll => await StopAllMcpServersAsync(),
                BasicEnum.ReadMcpSettingsFile => await ReadMcpSettingsAsync(),
                BasicEnum.WriteMcpSettings => await WriteMcpSettingsAsync(vsRequest.Payload),
                BasicEnum.OpenMcpSettings => await OpenMcpSettingsAsync(),
                _ => new VsResponse { Success = false, Payload = "Unknown action" }
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
                Payload = ex.Message
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
                Payload = "No active document"
            };

        return await ReadFileAsync(new List<ReadFileParams>
        {
            { new ReadFileParams { Path = docView.Document.FilePath } }
        });
    }

    /// <summary>
    /// Максимальное количество строк для чтения одного файла без явного указания LineCount.
    /// Предотвращает возврат слишком больших файлов в контекст LLM и localStorage.
    /// </summary>
    private const int MaxFileLines = 2_000;

    private async Task<VsResponse> ReadFileAsync(List<ReadFileParams>? fileParamsList, bool onlyContent = false)
    {
        var solutionPath = await GetSolutionPathAsync();

        // null = `files` не распарсился. GetObject глотает JsonException и возвращает default,
        // поэтому сюда приходит null, а не пустой список; раньше это был NullReferenceException
        // с текстом «Object reference not set…», то есть отказ хоста без единого слова о причине.
        if (fileParamsList == null)
        {
            return Refuse("Failed to read 'files': each entry needs a 'path' string. Nothing was read.");
        }

        if (fileParamsList.Count == 0)
        {
            return new VsResponse { Success = false, Payload = "No files specified." };
        }

        // Ключ дедупа — ВЕСЬ запрос, а не только путь: два диапазона одного файла — это
        // два разных чтения, и старый ключ молча выбрасывал второе.
        fileParamsList =
        [
            .. fileParamsList
                .GroupBy(x => (x.Path, x.StartLine, x.LineCount))
                .Select(g => g.First())
        ];

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        // onlyContent mode (e.g. GetAgents) — return raw text, not structured JSON
        if (onlyContent)
        {
            var sb = new StringBuilder();
            var isSuccess = true;
            foreach (var rp in fileParamsList)
            {
                var absPath = GetAbsolutePath(rp.Path, solutionPath);
                if (!File.Exists(absPath))
                {
                    sb.AppendLine($"File \"{rp.Path}\" doesn't exist.");
                    isSuccess = false;
                    continue;
                }
                try
                {
                    sb.Append(File.ReadAllText(absPath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    // Ожидаемый отказ (нет прав, файл занят, path is a directory): WARN, а не ERROR.
                    // ERROR в VS ещё и активирует панель Output — это читается как сбой хоста.
                    await Logger.LogAsync($"Error reading file {rp.Path}: {ex.Message}", "WARN");
                    sb.AppendLine($"Error reading file {rp.Path}: {ex.Message}");
                    isSuccess = false;
                }
            }
            return new VsResponse
            {
                Success = isSuccess,
                // Бинарный контракт: при отказе весь текст — причина отказа, при успехе — контент.
                Payload = sb.ToString()
            };
        }

        // Structured mode — return JSON array of FileContent.
        // Success = true if at least one file was read successfully (partial success).
        // Individual file errors are embedded in FileContent.Error fields.
        var results = new List<FileContent>();

        foreach (var rp in fileParamsList)
        {
            var absPath = GetAbsolutePath(rp.Path, solutionPath);

            if (!File.Exists(absPath))
            {
                results.Add(new FileContent { Path = rp.Path, Error = $"File doesn't exist." });
                continue;
            }

            try
            {
                var allLines = File.ReadLines(absPath);
                var totalLineCount = 0;

                var needsLimit = rp.LineCount <= 0;
                List<string> materializedLines = null;
                if (needsLimit)
                {
                    materializedLines = [.. allLines];
                    totalLineCount = materializedLines.Count;
                }

                var skipCount = rp.StartLine > 0 ? Math.Max(0, rp.StartLine - 1) : 0;
                var takeCount = rp.LineCount > 0 ? rp.LineCount : (needsLimit ? MaxFileLines : int.MaxValue);

                IEnumerable<string> lines;
                if (materializedLines != null)
                {
                    lines = materializedLines.Skip(skipCount).Take(takeCount);
                }
                else
                {
                    lines = allLines.Skip(skipCount).Take(takeCount);
                }

                var fileLines = lines.ToList();
                var fc = new FileContent
                {
                    Path = rp.Path,
                    Lines = fileLines,
                    StartLine = skipCount > 0 ? skipCount + 1 : null
                };

                // Если файл был обрезан — сохраняем общее количество строк
                if (needsLimit && totalLineCount > MaxFileLines && skipCount + fileLines.Count < totalLineCount)
                {
                    fc.TotalLines = totalLineCount;
                }

                results.Add(fc);
            }
            catch (Exception ex)
            {
                // Ожидаемый отказ чтения — WARN (ошибка всё равно уйдёт в FileContent.Error).
                await Logger.LogAsync($"Error reading file {rp.Path}: {ex.Message}", "WARN");
                results.Add(new FileContent { Path = rp.Path, Error = ex.Message });
            }
        }

        // Always return Success = true with structured results — even if all files failed.
        // The LLM needs to see FileContent.Error fields to know which files don't exist.
        // (VsBridge передаёт такой ответ как есть и не превращает его в исключение, но
        // структурированные ошибки всё равно информативнее одного текста.)
        return new VsResponse
        {
            Success = true,
            Payload = JsonSerializer.Serialize(results)
        };
    }

    // Обычная readonly-структура, а не `record struct`: позиционный record требует init-сеттеров,
    // то есть System.Runtime.CompilerServices.IsExternalInit, которого нет ни в net48, ни в ToolCore.
    /// <summary>
    /// Резолвнутая цель файловой операции: абсолютный путь для файлового вызова плюс два поля,
    /// которые нужны отчёту — имя ровно в том виде, как его прислала модель, и корень решения.
    /// </summary>
    private readonly struct FileTarget(string solutionPath, string inputName, string absolutePath)
    {
        public string SolutionPath { get; } = solutionPath;

        /// <summary>Имя для отчётов: то, что прислала модель, а не резолвнутый путь.</summary>
        public string InputName { get; } = inputName;

        public string AbsolutePath { get; } = absolutePath;
    }

    /// <summary>
    /// Один и тот же триптих (корень решения → имя из payload → абсолютный путь) стоял в трёх
    /// файловых инструментах копипастой. <see cref="GetAbsolutePath"/> бросает на пустом пути,
    /// поэтому вызывать это надо ВНУТРИ try: пустой filePath обязан стать именованным отказом
    /// (WARN), а не ERROR внешнего catch.
    /// </summary>
    private async Task<FileTarget> ResolveFileTargetAsync(IReadOnlyDictionary<string, object> args, string key)
    {
        var solutionPath = await GetSolutionPathAsync();
        var inputName = args.GetString(key);
        return new FileTarget(solutionPath, inputName, GetAbsolutePath(inputName, solutionPath));
    }

    /// <summary>Отказ инструмента: Success = false, payload — причина, которую прочитает модель.</summary>
    private static VsResponse Refuse(string payload) => new() { Success = false, Payload = payload };

    /// <summary>
    /// Отказ, вызванный исключением файловой операции (нет прав, файл занят, путь пуст).
    /// WARN, а не ERROR: это ожидаемый исход вызова, а ERROR в VS ещё и активирует панель Output.
    /// </summary>
    private async Task<VsResponse> RefuseAsync(string payload, Exception exception)
    {
        await Logger.LogAsync(exception.Message, "WARN");
        return Refuse(payload);
    }

    private async Task<VsResponse> CreateNewFileAsync(IReadOnlyDictionary<string, object> args)
    {
        // `content` отсутствующий и `content` пустой — РАЗНЫЕ вещи, и склеивать их нельзя:
        // null превращался в "", а overwrite с пустым телом СОЗНАТЕЛЬНО не отказывает (создание
        // пустого файла — легальная операция), поэтому существующий файл затирался до нуля байт
        // с ответом «overwritten successfully». Симметрично отказу `newStr: null` в edit_file.
        var content = args.GetString("content");
        if (content is null)
            return Refuse("'content' is missing: send the full file text (an empty string is a valid body). Nothing was written.");

        var mode = args.GetString("mode");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var target = await ResolveFileTargetAsync(args, "filePath");

            // FileWriter — кодировка и концы строк берутся из `.editorconfig` (для нового файла)
            // или из самого файла (для существующего). Раньше здесь было File.WriteAllText(...)
            // с Encoding.UTF8, то есть BOM — в дереве с `charset = utf-8` это невидимый дефект.
            var result = await _fileWriter.WriteAsync(target.AbsolutePath, content, mode);
            if (!result.Success)
                await Logger.LogAsync(result.Payload, "WARN");

            return new VsResponse
            {
                Success = result.Success,
                Payload = result.Payload
            };
        }
        catch (Exception e)
        {
            // Ожидаемый отказ файловой операции (нет прав, путь занят) — WARN, не ERROR.
            return await RefuseAsync(e.Message, e);
        }
    }

    private async Task<VsResponse> DeleteFileAsync(IReadOnlyDictionary<string, object> args)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var target = await ResolveFileTargetAsync(args, "path");
            if (!File.Exists(target.AbsolutePath))
                return Refuse($"File {target.InputName} does not exist.");

            File.Delete(target.AbsolutePath);
            return new VsResponse { Payload = $"File {target.InputName} deleted successfully." };
        }
        catch (Exception e)
        {
            // Ожидаемый отказ удаления (нет прав, файл занят) — WARN, не ERROR.
            return await RefuseAsync(e.Message, e);
        }
    }

    private async Task<VsResponse> ExecAsync(IReadOnlyDictionary<string, object> args)
    {
        var param = args.GetString("command");
        var solutionPath = await GetSolutionPathAsync();

        // Лимит приходит из настроек UI общим полем timeoutMs (правило канала — ToolTimeout.Resolve):
        // отсутствующее или неположительное значение означает дефолт инструмента, а не «без лимита».
        var timeoutMs = ToolTimeout.Resolve(args.GetInt("timeoutMs"), ToolTimeout.DefaultCommandMs);

        // Не переключаемся на главный поток — запуск процесса не требует UI-потока,
        // а блокировка UI-потока на время выполнения недопустима
        var result = await _processExecutor.ExecuteBashAsync(param, solutionPath, timeoutMs);

        // Одно поле Payload на оба случая: stdout и stderr склеиваем. Раньше при ненулевом коде
        // возврата UI показывал только stderr, а stdout терялся — теперь виден весь вывод.
        // ВАЖНО: у string.Join нет перегрузки (string, IEnumerable, TimeSpan) — любой ЛИШНИЙ
        // аргумент молча уводит вызов в params object[] и печатает ToString() каждого элемента:
        // result = "System.Linq.Enumerable+WhereArrayIterator`1[System.String]\n00:00:00.1000000".
        var payload = string.Join("\n",
            new[] { TruncateOutput(result.Output), TruncateOutput(result.Error) }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

        return new VsResponse
        {
            Success = result.Success && !result.TimedOut && !result.Cancelled,
            Payload = payload
        };
    }

    /// <summary>
    /// Обрезает вывод команды до разумного лимита, сохраняя конец (там обычно ошибки/результаты).
    /// </summary>
    private const int MaxOutputLength = 30_000; // ~30 KB

    private static string TruncateOutput(string output)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= MaxOutputLength)
            return output;

        var truncated = output.Length - MaxOutputLength;
        return $"[... {truncated:N0} characters truncated from the beginning ...]\n{output.Substring(output.Length - MaxOutputLength)}";
    }

    private static string TruncateLine(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
    }

    private async Task<VsResponse> SearchFilesAsync(IReadOnlyDictionary<string, object> args)
    {
        var pattern = args.GetString("regex");
        var maxMatches = args.GetInt("maxMatches", 50);
        if (string.IsNullOrEmpty(pattern))
        {
            return new VsResponse
            {
                Success = false,
                Payload = "Regex pattern should be not empty."
            };
        }

        var solutionPath = await GetSolutionPathAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
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

        for (var i = 0; i < matchedFiles.Count; i++)
        {
            if (i > maxMatches)
            {
                sb.AppendLine($"+{i - maxMatches} files. Limited to {maxMatches}");
                break;
            }
            var file = matchedFiles[i];
            sb.AppendLine($"{file.Path} [{file.SizeKB:F1} KB]");
        }

        return new VsResponse { Payload = sb.ToString() };
    }

    private async Task<VsResponse> GrepSearchAsync(IReadOnlyDictionary<string, object> args)
    {
        var query = args.GetString("regex");
        var contextLines = args.GetInt("contextLines", 3);
        var maxMatches = args.GetInt("maxMatches", 50);

        if (string.IsNullOrEmpty(query))
        {
            return new VsResponse
            {
                Success = false,
                Payload = "Parameter 'query' is required."
            };
        }

        var solutionPath = await GetSolutionPathAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var files = await GetAllSolutionFilesAsync();
        var regex = new Regex(query, RegexOptions.Multiline, TimeSpan.FromMilliseconds(100));
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

                    var start = Math.Max(0, lineIdx - contextLines);
                    var end = Math.Min(lines.Length - 1, lineIdx + contextLines);

                    for (var i = start; i <= end; i++)
                    {
                        var marker = i == lineIdx ? ">" : " ";
                        var num = (i + 1).ToString().PadLeft(4);
                        sb.AppendLine($"{marker} {num} | {TruncateLine(lines[i], maxLength: 200)}");
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

        return new VsResponse { Payload = TruncateOutput(header + sb.ToString()) };
    }

    private async Task<VsResponse> FindDeclarationsAsync(IReadOnlyDictionary<string, object> args)
    {
        var symbolName = args.GetString("symbol");

        try
        {
            // Вызов изолированного сервиса (сборка Roslyn загрузится только здесь)
            var service = new RoslynSearchService();
            return await service.FindDeclarationsAsync(ServiceProvider.GlobalProvider, symbolName);
        }
        catch (FileNotFoundException ex)
        {
            await Logger.LogAsync($"Roslyn compatibility error: {ex.Message}", "ERROR");
            return new VsResponse
            {
                Success = false,
                Payload = $"Roslyn compatibility error in VS 2026. File not found: {ex.FileName}"
            };
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(ex.Message, "ERROR");
            return new VsResponse { Success = false, Payload = $"Critical error: {ex.Message}" };
        }
    }

    private async Task<VsResponse> FindReferencesAsync(IReadOnlyDictionary<string, object> args)
    {
        var symbolName = args.GetString("symbol");

        try
        {
            var service = new RoslynSearchService();
            return await service.FindReferencesAsync(ServiceProvider.GlobalProvider, symbolName);
        }
        catch (FileNotFoundException ex)
        {
            await Logger.LogAsync($"Roslyn compatibility error: {ex.Message}", "ERROR");
            // Перехватываем критическую ошибку несовместимости сборок Roslyn в VS 2022 и 2026
            return new VsResponse
            {
                Success = false,
                Payload = $"Roslyn compatibility error in VS 2026. File not found: {ex.FileName}. Message: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            await Logger.LogAsync(ex.Message, "ERROR");
            return new VsResponse { Success = false, Payload = $"Critical error: {ex.Message}" };
        }
    }

    private async Task<VsResponse> ListDirectoryAsync(IReadOnlyDictionary<string, object> args)
    {
        var recursive = args.GetBool("recursive");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var target = await ResolveFileTargetAsync(args, "path");
            if (!Directory.Exists(target.AbsolutePath))
                return Refuse($"Directory {target.InputName} doesn't exist");

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var items = string.Join(Environment.NewLine, Directory.GetFileSystemEntries(target.AbsolutePath, "*", searchOption)
                .Select(f => MakeRelativeToSolution(f, target.SolutionPath)));

            return new VsResponse
            {
                Payload = items
            };
        }
        catch (Exception e)
        {
            // Ожидаемый отказ (нет каталога, пустой путь) — WARN, не ERROR.
            return await RefuseAsync(e.Message, e);
        }
    }

    /// <summary>
    /// `edit_file` = parse → resolve → read → <see cref="EditBatch"/> → dry-run / отказ → write.
    /// Вся работа с правками в памяти (валидация, порядок, счётчики, текст отчёта) — в
    /// ToolCore.EditBatch, под тестами: этот хост юнит-тестами не покрывается.
    /// </summary>
    private async Task<VsResponse> EditFileAsync(IReadOnlyDictionary<string, object> args)
    {
        // Пустой список и нечитаемый список — РАЗНЫЕ отказы: в первом модель прислала валидный,
        // но пустой батч, во втором невалидно хотя бы одно поле внутри правки. Раньше оба случая
        // давали «Failed to get replacements» — без файла, без причины, без индекса правки.
        //
        // Вторая половина того же урока: отказ НЕ ДОЛЖЕН угадывать причину. Готовая фраза
        // «each edit needs 'oldStr' and 'newStr' strings» утверждала это на payload, где оба поля
        // были на месте, и отправляла модель чинить не то. Теперь впереди стоит настоящая ошибка
        // разбора, а подсказка про схему остаётся хвостом.
        var diffEdits = args.GetObject<List<DiffEdit>>("edits", out var editError);
        if (diffEdits is null)
            return Refuse($"Failed to read 'edits': {editError}. " +
                          "Each edit needs 'oldStr' and 'newStr' strings; 'approximateLine' takes an integer " +
                          "and 'replaceAll' a boolean (both may be null or omitted). Nothing was changed.");

        if (diffEdits.Count == 0)
            return Refuse("'edits' is empty: nothing to apply.");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        FileTarget target;
        try
        {
            target = await ResolveFileTargetAsync(args, "filePath");
        }
        catch (Exception e)
        {
            // Пустой filePath отказывает здесь, а не в внешнем catch: это отказ вызова.
            return await RefuseAsync($"edit NOT attempted on {args.GetString("filePath")}: {e.Message}", e);
        }

        // File.Exists для каталога — false: без этой проверки edit_file на папке отвечал
        // «File doesn't exist.» и модель шла искать то, что уже нашла.
        if (Directory.Exists(target.AbsolutePath))
            return Refuse("Path is a directory, not a file.");

        if (!File.Exists(target.AbsolutePath))
            return Refuse("File doesn't exist.");

        FileReadResult fileData;
        try
        {
            fileData = await _fileUtils.ReadFileWithMetadataAsync(target.AbsolutePath);
        }
        catch (Exception e)
        {
            // Ожидаемый отказ чтения (нет прав, файл занят) — WARN, не ERROR.
            return await RefuseAsync($"edit NOT attempted on {target.InputName}: {e.Message}", e);
        }

        // Отказ чтения (нет файла, слишком большой, бинарный) — отчёт ДО применения правок: иначе
        // «oldStr not found» отправлял модель искать контекст в файле, который вообще не читался.
        if (fileData.Status != FileReadStatus.Ok)
            return Refuse($"edit NOT attempted on {target.InputName}: {fileData.SkipReason ?? fileData.Status.ToString()}");

        var batch = EditBatch.Apply(diffEdits, fileData.Lines);
        batch.File = target.InputName;

        // Dry-run: правки применяются к строкам в памяти, файл НЕ пишется.
        if (args.GetBool("dryRun"))
            return new VsResponse { Payload = batch.FormatReport(dryRun: true) };

        // Ноль применённых правок — отказ ДО записи: иначе файл переписывается без изменений,
        // а модель читает «Changes successfully applied» о правке, которой не было.
        if (!batch.AnyApplied)
            return Refuse(batch.FormatReport(dryRun: false));

        try
        {
            await _fileUtils.SaveFileAsync(target.AbsolutePath, fileData.Lines, fileData.Encoding, fileData.Separator, fileData.HasFinalNewLine);
            await OpenEditorAsync(target.AbsolutePath);
            await Logger.LogAsync($"{batch.TotalReplacements} changes successfully applied to {target.InputName}.");
        }
        catch (Exception e)
        {
            // Ожидаемый отказ записи (файл занят, read-only) — WARN, не ERROR.
            return await RefuseAsync($"Changes to {target.InputName} is FAILED. {e.Message}", e);
        }

        // Предупреждаем о нормализации строк ТОЛЬКО когда она реально произошла. Сам текст
        // предупреждения собирает EditBatch: предложение для модели не должно существовать в двух
        // хостах, иначе оно в них разойдётся.
        var normalisedSeparator = fileData.HasMixedLineEndings ? fileData.Separator : null;

        return new VsResponse { Payload = batch.FormatReport(dryRun: false, normalisedSeparator) };
    }

    /// <summary>
    /// Открываем файл в редакторе Visual Studio
    /// </summary>
    public async Task OpenEditorAsync(string filepath)
    {
        try
        {
            await VS.Documents.OpenAsync(filepath);
        }
        catch (Exception)
        {
            // иногда бывает - игнорим
        }
    }

    private async Task<VsResponse> BuildSolutionAsync()
    {
        var result = await VS.Build.BuildSolutionAsync(Toolkit.BuildAction.Rebuild);

        if (!result)
        {
            await Task.Delay(2000); // ошибки не сразу появляются
            var errorList = await GetBuildErrorListAsync();
            return new VsResponse
            {
                Success = false,
                Payload = $"""
                         Rebuild is failed.
                         ---
                         Errors:
                         {errorList}
                         """
            };
        }

        return new VsResponse
        {
            Payload = "Rebuild is successful."
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
            var exe = Path.ChangeExtension(fullDllPath, "exe");

            // Если есть EXE (например xUnit v3) — запускаем через него,
            // DLL не добавляем, чтобы избежать дублирования запуска тестов
            if (File.Exists(exe))
            {
                exePaths.Add(exe);
            }
            else if (File.Exists(fullDllPath))
            {
                dllPaths.Add(fullDllPath);
            }
        }
        return (dllPaths, exePaths);
    }

    /// <summary>
    /// Имя активной конфигурации решения (Debug/Release) через DTE automation model.
    /// null — недоступна: проверка не должна ломать запуск тестов.
    /// </summary>
    private async Task<string?> GetActiveConfigurationNameAsync()
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            return dte?.Solution?.SolutionBuild?.ActiveConfiguration?.Name;
        }
        catch
        {
            // safe skip error
            return null;
        }
    }

    /// <summary>
    /// Запуск тестов через консоль
    /// Через TestExplorer можно запустить, но не узнать о завершении и прочитать ошибки тоже нельзя
    /// </summary>
    /// <returns></returns>
    private async Task<VsResponse> RunTestsAsync()
    {
        // Для начала запуск билда
        var build = await BuildSolutionAsync();
        if (!build.Success)
        {
            return build;
        }

        var solutionPath = await GetSolutionPathAsync();
        var (testDlls, testExes) = await GetTestAssembliesAsync();

        if (testDlls.Count == 0 && testExes.Count == 0)
        {
            return new VsResponse
            {
                Success = false,
                Payload = "No test assemblies found."
            };
        }

        var allOutput = new StringBuilder();
        var hasFailures = false;

        // Предупреждение агенту (в текст результата, не пользователю): тесты собираются
        // только при конфигурации Debug. При Release прогон молча даёт устаревшие
        // результаты — новые/изменённые тесты могут не появиться.
        var configName = await GetActiveConfigurationNameAsync();
        if (!string.IsNullOrEmpty(configName) && !configName.Equals("Debug", StringComparison.OrdinalIgnoreCase))
        {
            allOutput.AppendLine($"WARNING: active solution configuration is '{configName}', not 'Debug'. Test assemblies may be stale — new or changed tests may be missing from this run.");
            allOutput.AppendLine();
        }

        // Запуск dotnet test для всех DLL
        if (testDlls.Count > 0)
        {
            var allDlls = string.Join(" ", testDlls.Select(d => $"\"{d}\""));
            await Logger.LogAsync($"Run dotnet test for {allDlls}");

            var (success, stdout, stderr) = await RunTestProcessAsync("dotnet", $"test {allDlls} --nologo --logger \"console;verbosity=normal\"", solutionPath);
            allOutput.AppendLine("=== dotnet test ===");
            allOutput.AppendLine(stdout);
            if (!success)
            {
                hasFailures = true;
                allOutput.AppendLine(stderr);
            }
        }

        // Запуск каждого EXE индивидуально (xUnit и подобные)
        foreach (var exe in testExes)
        {
            await Logger.LogAsync($"Run xUnit exe {exe}");
            var (success, stdout, stderr) = await RunTestProcessAsync(exe, "", solutionPath);
            allOutput.AppendLine($"=== {Path.GetFileName(exe)} ===");
            allOutput.AppendLine(stdout);
            if (!success)
            {
                hasFailures = true;
                allOutput.AppendLine(stderr);
            }
        }

        return hasFailures
            ? new VsResponse { Success = false, Payload = TruncateOutput(allOutput.ToString()) }
            : new VsResponse { Payload = TruncateOutput(allOutput.ToString()) };
    }

    /// <summary>
    /// Запуск процесса тестирования и сбор его вывода.
    /// </summary>
    private async Task<(bool Success, string Stdout, string Stderr)> RunTestProcessAsync(
        string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        // Полный набор переменных для неинтерактивного режима
        ProcessExecutor.ConfigureNonInteractiveEnvironment(startInfo);

        using var process = Process.Start(startInfo);
        if (process == null)
            return (false, "", "Failed to start process");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return (process.ExitCode == 0, await outputTask, await errorTask);
    }

    private async Task<VsResponse> GetSolutionStructureAsync()
    {
        return new VsResponse
        {
            Payload = string.Join("\n", await SolutionStructure.BuildStructureAsync())
        };
    }

    private async Task<VsResponse> GetProjectInfoAsync()
    {
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

    // Git-инструменты идут через тот же ExecAsync, поэтому лимит у них тот же — из поля timeoutMs
    // (его кладёт BuiltInAgent.WithToolTimeout). Без явного переноса поля каждая git-команда
    // молча получала бы дефолт: настройка снова игнорировалась бы, только тише.
    private async Task<VsResponse> GitStatusAsync(IReadOnlyDictionary<string, object> args)
    {
        return await ExecAsync(new Dictionary<string, object>()
        {
            ["command"] = "git status",
            ["timeoutMs"] = args.GetInt("timeoutMs")
        });
    }

    private async Task<VsResponse> GitLogAsync(IReadOnlyDictionary<string, object> args)
    {
        var limit = args.GetString("number") ?? "20";
        return await ExecAsync(new Dictionary<string, object>()
        {
            ["command"] = $"git log -n {limit} --pretty=format:\"%h - %s | %ad\" --stat --date=short",
            ["timeoutMs"] = args.GetInt("timeoutMs")
        });
    }

    private async Task<VsResponse> GitDiffAsync(IReadOnlyDictionary<string, object> args)
    {
        var revisions = args.GetString("revisions");
        return await ExecAsync(new Dictionary<string, object>()
        {
            ["command"] = $"git diff {revisions}",
            ["timeoutMs"] = args.GetInt("timeoutMs")
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
            return new VsResponse { Success = false, Payload = $"Error reading rules.md: {ex.Message}" };
        }
    }

    private async Task<VsResponse> OpenFileInEditorAsync(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return new VsResponse { Success = false, Payload = "File path is empty" };
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
            return new VsResponse { Success = false, Payload = $"Error opening {relativePath}: {ex.Message}" };
        }
    }

    private async Task<VsResponse> OpenFolderInExplorerAsync(string relativePath)
    {
        var solutionPath = await GetSolutionPathAsync();
        var folderPath = string.IsNullOrEmpty(relativePath) ? solutionPath : GetAbsolutePath(relativePath, solutionPath);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            Directory.CreateDirectory(folderPath);
#pragma warning disable S4036
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
                return new VsResponse { Success = false, Payload = $"Path {folderPath} does not exist" };
            }
#pragma warning restore S4036
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Payload = $"Error opening folder: {ex.Message}" };
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
    /// Если указан fileName — читает конкретный файл из папки скилла
    /// </summary>
    private async Task<VsResponse> ReadSkillContentAsync(IReadOnlyDictionary<string, object> args)
    {
        var skillName = args.GetString("skillName");
        var fileName = args.GetString("fileName");

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!_skillPathByName.TryGetValue(skillName, out var fullPath))
        {
            return new VsResponse
            {
                Success = false,
                Payload = $"Skill file not found: {skillName}"
            };
        }

        if (!File.Exists(fullPath))
        {
            return new VsResponse
            {
                Success = false,
                Payload = $"Skill file not exits: {skillName}"
            };
        }

        var skillFolder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(skillFolder))
        {
            return new VsResponse
            {
                Success = false,
                Payload = $"Cannot determine skill folder for: {skillName}"
            };
        }

        // If fileName is provided — read a specific file from the skill folder
        if (!string.IsNullOrEmpty(fileName))
        {
            // Security: resolve path relative to skill folder and verify it stays within
            var canonicalSkillFolder = Path.GetFullPath(skillFolder);
            var targetPath = Path.GetFullPath(Path.Combine(skillFolder, fileName));

            if (!targetPath.StartsWith(canonicalSkillFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return new VsResponse
                {
                    Success = false,
                    Payload = $"Invalid file path: '{fileName}' escapes the skill folder."
                };
            }

            if (!File.Exists(targetPath))
            {
                return new VsResponse
                {
                    Success = false,
                    Payload = $"File '{fileName}' not found in skill '{skillName}'."
                };
            }

            try
            {
                var content = File.ReadAllText(targetPath, Encoding.UTF8);
                return new VsResponse { Payload = content };
            }
            catch (Exception ex)
            {
                return new VsResponse
                {
                    Success = false,
                    Payload = $"Error reading file '{fileName}': {ex.Message}"
                };
            }
        }

        // No fileName — return SKILL.md content + list of all files in skill folder
        try
        {
            var contentLines = File.ReadAllLines(fullPath, Encoding.UTF8);
            var (name, description, headerLines) = ParseYamlFrontmatter(contentLines);
            var markdownContent = string.Join("\n", contentLines.Skip(headerLines));

            // Recursively scan skill folder for all files except SKILL.md itself
            var files = new List<string>();
            var skillFileName = Path.GetFileName(fullPath);
            if (Directory.Exists(skillFolder))
            {
                foreach (var file in Directory.EnumerateFiles(skillFolder, "*", SearchOption.AllDirectories))
                {
                    // Compute relative path manually (Path.GetRelativePath may not be available in all target frameworks)
                    var relativePath = file.Substring(skillFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    // Skip SKILL.md itself (case-insensitive)
                    if (relativePath.Equals(skillFileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    files.Add(relativePath.Replace('\\', '/'));
                }
            }

            var skillContent = new SkillContent
            {
                Name = name,
                Description = description,
                Content = markdownContent,
                Files = files
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
                Payload = $"Error reading skill content: {ex.Message}"
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

    private async Task<VsResponse> StopAllMcpServersAsync()
    {
        if (!McpHostRuntime.Supervisor.IsConnected)
        {
            return new VsResponse { Success = true, Payload = "All MCP servers stopped." };
        }

        return await SendToMcpHostAsync(McpHostMethods.StopAll, null, TimeSpan.FromSeconds(30));
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
            return new VsResponse { Success = false, Payload = $"Error reading mcp.json: {ex.Message}" };
        }
    }

    private async Task<VsResponse> WriteMcpSettingsAsync(string content)
    {
        try
        {
            if (string.IsNullOrEmpty(content))
            {
                return new VsResponse { Success = false, Payload = "Content is empty" };
            }

            var filePath = GetMcpSettingsPath();
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await StopAllMcpServersAsync();

            // UTF-8 БЕЗ BOM: mcp.json читают другие агенты, а BOM ломает строгие JSON-парсеры.
            // Файл лежит вне дерева (~/.agents), поэтому .editorconfig к нему не применим.
            File.WriteAllText(filePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new VsResponse { Payload = "OK" };
        }
        catch (Exception ex)
        {
            return new VsResponse { Success = false, Payload = $"Error writing mcp.json: {ex.Message}" };
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
                var sample = "{\n  \"mcp\": {}\n}";
                File.WriteAllText(filePath, sample);
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
            return new VsResponse { Success = false, Payload = $"Error opening mcp.json: {ex.Message}" };
        }
    }

    private async Task<VsResponse> SendToMcpHostAsync(string method, object? parameters, TimeSpan timeout)
    {
        try
        {
            var supervisor = McpHostRuntime.Supervisor;
            await supervisor.EnsureStartedAsync();

            var response = await supervisor.SendRequestAsync(method, parameters, timeout);
            return new VsResponse
            {
                Success = response.Success,
                // Успех — JSON-результат; отказ — текст ошибки. Одно поле, дискриминатор Success.
                Payload = response.Success
                    ? response.Result is { } result ? result.GetRawText() : null
                    : response.Error
            };
        }
        catch (McpHostStartupException ex)
        {
            await Logger.LogAsync($"MCP host startup failed: {ex.UserMessage}", "ERROR");
            return new VsResponse { Success = false, Payload = ex.UserMessage };
        }
        catch (TimeoutException)
        {
            return new VsResponse { Success = false, Payload = $"MCP host request '{method}' timed out." };
        }
        catch (Exception ex)
        {
            await Logger.LogAsync($"MCP host request '{method}' failed: {ex.Message}", "ERROR");
            return new VsResponse { Success = false, Payload = ex.Message };
        }
    }

    private async Task<VsResponse> McpGetToolsAsync(IReadOnlyDictionary<string, object> args)
    {
        var serverId = args.GetString("serverId");
        var command = args.GetString("command");
        var arguments = args.GetObject<List<string>>("args") ?? [];
        var env = args.GetDictionary("env");
        var url = args.GetString("url");
        var headers = args.GetDictionary("headers");

        if (string.IsNullOrEmpty(serverId))
        {
            return new VsResponse { Success = false, Payload = "serverId is required" };
        }

        var solutionPath = await GetSolutionPathAsync();
        return await SendToMcpHostAsync(McpHostMethods.ListTools, new McpListToolsParams
        {
            ServerId = serverId,
            Command = command ?? string.Empty,
            Args = arguments,
            WorkingDirectory = solutionPath,
            Url = url,
            Headers = headers,
            Env = env,
        }, TimeSpan.FromSeconds(90));
    }

    private async Task<VsResponse> McpCallToolAsync(IReadOnlyDictionary<string, object> args)
    {
        var serverId = args.GetString("serverId");
        var toolName = args.GetString("toolName");
        var toolArgsRaw = args.GetValue("arguments");
        // То же правило канала, что и у bash, но другой дефолт инструмента: MCP-вызовы бывают
        // дальними и тяжёлыми, две минуты там режут нормальную работу.
        var timeoutMs = ToolTimeout.Resolve(args.GetInt("timeoutMs"), ToolTimeout.DefaultMcpCallMs);
        object? toolArgs = null;

        if (toolArgsRaw is string jsonStr && !string.IsNullOrEmpty(jsonStr))
        {
            toolArgs = JsonSerializer.Deserialize<object>(jsonStr);
        }
        else if (toolArgsRaw != null)
        {
            toolArgs = toolArgsRaw;
        }

        var command = args.GetString("command");
        var commandArgs = args.GetObject<List<string>>("args") ?? [];
        var env = args.GetDictionary("env");
        var url = args.GetString("url");
        var headers = args.GetDictionary("headers");

        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(toolName))
        {
            return new VsResponse { Success = false, Payload = "serverId and toolName are required" };
        }

        var solutionPath = await GetSolutionPathAsync();
        return await SendToMcpHostAsync(McpHostMethods.CallTool, new McpCallToolParams
        {
            ServerId = serverId,
            Command = command ?? string.Empty,
            Args = commandArgs,
            WorkingDirectory = solutionPath,
            Url = url,
            Headers = headers,
            Env = env,
            ToolName = toolName,
            Arguments = toolArgs as JsonElement?,
            TimeoutMs = timeoutMs,
        }, TimeSpan.FromMilliseconds(timeoutMs) + TimeSpan.FromMilliseconds(ToolTimeout.McpHostGraceMs));
    }

    private class SearchFileInfo
    {
        public string Path { get; set; }
        public string Extension { get; set; }
        public double SizeKB { get; set; }
    }
}
