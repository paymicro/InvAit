using UIBlazor.Services.Settings;

namespace UIBlazor.Services;

public class SystemPromptBuilder(
    ICommonSettingsProvider commonSettingsProvider,
    IProfileManager profileManager,
    IToolManager toolManager,
    ISkillService skillService,
    IRuleService ruleService,
    IVsCodeContextService vsCodeContextService) : ISystemPromptBuilder
{
    public ConnectionProfile Options => profileManager.ActiveProfile;

    public async Task<string> PrepareSystemPromptAsync(AppMode mode, CancellationToken cancellationToken)
    {
        // Загружаем метаданные скиллов и добавляем в системный промпт
        var skillsMetadata = await skillService.GetSkillsMetadataAsync(cancellationToken);
        var skillsSection = skillService.FormatSkillsForSystemPrompt(skillsMetadata);

        var contextSection = new StringBuilder();
        var currentContext = vsCodeContextService.CurrentContext;
        if (currentContext != null)
        {
            var codeContext = new List<string>();
            if (commonSettingsProvider.Current.SendSolutionsStricture && currentContext.SolutionFiles.Count > 0)
            {
                codeContext.Add($"""
                                Solution structure:
                                ```
                                {BuildSolutionFiles(currentContext, true)}
                                ```
                                """);
            }
            if (commonSettingsProvider.Current.SendCurrentFile && !string.IsNullOrEmpty(currentContext.ActiveFilePath))
            {
                codeContext.Add($"""
                                ## Current (active) file
                                - Path: {currentContext.ActiveFilePath}
                                - Selected lines: {currentContext.SelectionStartLine} - {currentContext.SelectionEndLine}
                                ```
                                {currentContext.ActiveFileContent}
                                ```
                                """);
            }
            if (codeContext.Count > 0)
            {
                contextSection.AppendLine("# CURRENT CODE CONTEXT");
                foreach (var item in codeContext)
                {
                    contextSection.AppendLine(item);
                }
            }
        }

        // Загружаем правила
        var rules = await ruleService.GetRulesAsync(cancellationToken);
        // файл agents.md
        var agents = await ruleService.GetAgentsMdAsync(cancellationToken);

        List<string?> systemPromptBlocks = [Options.SystemPrompt,
            toolManager.GetToolUseSystemInstructions(mode, skillsMetadata.Count != 0),
            skillsSection,
            contextSection.ToString(),
            rules,
            !string.IsNullOrEmpty(agents) ? string.Join("# Agents instructions\n", agents) : null,
            $"Current date: {DateTime.Now:f}"];

        return string.Join(Environment.NewLine, systemPromptBlocks.Where(b => !string.IsNullOrEmpty(b)));
    }

    public string BuildSolutionFiles(VsCodeContext currentContext, bool compress)
    {
        var sb = new StringBuilder();
        var lastDir = string.Empty;
        var difPrefix = VsCodeContext.DirPrefix.AsSpan();
        foreach (var item in currentContext.SolutionFiles)
        {
            if (compress)
            {
                var itemSpan = item.AsSpan();
                var pathIndex = -1;
                if (item.StartsWith("Project"))
                {
                    lastDir = currentContext.SolutionPath;
                }
                else
                {
                    pathIndex = item.IndexOf(VsCodeContext.DirPrefix);
                }

                if (pathIndex != -1)
                {
                    // Берем часть после префикса и обрезаем пробелы без создания строк
                    var pathPart = itemSpan[(pathIndex + difPrefix.Length)..].TrimStart();
                    lastDir = pathPart.ToString();
                    // В строке с префиксом (папкой) выводим item целиком
                    sb.Append(item).Append('\n');
                }
                else
                {
                    var simplified = false;
                    if (!string.IsNullOrEmpty(lastDir))
                    {
                        // Ищем, где в строке файла начинается путь. 
                        // Если формат файла похож на папку (есть какой-то отступ/префикс),
                        // нужно найти индекс начала пути. Допустим, он всегда после какого-то символа 
                        // или просто ищем вхождение lastDir.
                        var dirPos = item.IndexOf(lastDir);
                        if (dirPos != -1)
                        {
                            // Пишем всё ДО пути + сам файл ПОСЛЕ пути
                            sb.Append(itemSpan[..dirPos])
                              .Append(itemSpan[(dirPos + lastDir.Length + (lastDir[^1] == '\\' ? 0 : 1))..])
                              .Append('\n');
                            simplified = true;
                        }
                    }

                    if (!simplified)
                    {
                        sb.Append(item).Append('\n');
                    }
                }
            }
            else
            {
                sb.Append(item).Append('\n');
            }
        }

        return sb.ToString();
    }
}
