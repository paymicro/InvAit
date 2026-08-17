namespace UIBlazor.Services;

public class SystemPromptBuilder(
    IProfileManager profileManager,
    IToolManager toolManager,
    ISkillService skillService,
    IRuleService ruleService,
    IVsCodeContextService vsCodeContextService) : ISystemPromptBuilder
{
    public ConnectionProfile Options => profileManager.ActiveProfile;

    public async Task<string> PrepareSystemPromptAsync(AppMode mode, CancellationToken cancellationToken)
    {
        var profile = profileManager.ActiveProfile;

        // Загружаем метаданные скиллов и добавляем в системный промпт
        var skillsMetadata = await skillService.GetSkillsMetadataAsync(cancellationToken);
        var skillsSection = profile.SendSkills
            ? skillService.FormatSkillsForSystemPrompt(skillsMetadata)
            : string.Empty;

        var contextSection = new StringBuilder();
        var currentContext = vsCodeContextService.CurrentContext;
        if (currentContext != null)
        {
            var codeContext = new List<string>();
            if (profile.SendSolutionStructure && currentContext.SolutionFiles.Count > 0)
            {
                codeContext.Add($"""
                                Solution structure:
                                ```
                                {BuildSolutionFiles(currentContext, true)}
                                ```
                                """);
            }
            if (profile.SendCurrentFile && !string.IsNullOrEmpty(currentContext.ActiveFilePath))
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
        var rules = profile.SendRules
            ? await ruleService.GetRulesAsync(cancellationToken)
            : string.Empty;
        // файл agents.md
        var agents = profile.SendAgentsMd
            ? await ruleService.GetAgentsMdAsync(cancellationToken)
            : string.Empty;

        // Mermaid instructions — independent of mode instructions
        var mermaidSection = profile.UseMermaidDiagrams
            ? "Use Mermaid diagrams for clarity in explanations. This will help you better visualize the answer formula. Don`t use \", {, }, (, ), [, ], in Mermaid node names."
            : string.Empty;

        // Mode instructions — independent of Mermaid
        var modeInstructions = profile.SendModeInstructions
            ? BuildModeInstructions(mode)
            : string.Empty;

        List<string?> systemPromptBlocks = [profile.SystemPrompt,
            mermaidSection,
            modeInstructions,
            skillsSection,
            contextSection.ToString(),
            rules,
            !string.IsNullOrEmpty(agents) ? string.Join("# Agents instructions\n", agents) : null,
            profile.SendCurrentDate ? $"Current date: {DateTime.Now:dd-MM-yyyy}" : null];

        return string.Join(Environment.NewLine, systemPromptBlocks.Where(b => !string.IsNullOrEmpty(b)));
    }

    /// <summary>
    /// Builds a system prompt for a sub-agent.
    /// Uses the LLM-provided <paramref name="customPrompt"/> as the base, then appends context sections
    /// from the active profile (rules, skills, solution structure, mode instructions, etc.).
    /// Exceptions vs main agent prompt:
    /// - Active file content is never included (sub-agent can use read_files tool).
    /// - Mermaid diagram instructions are never included (sub-agent returns text result to main agent).
    /// </summary>
    public async Task<string> PrepareSubAgentSystemPromptAsync(string customPrompt, CancellationToken cancellationToken)
    {
        var profile = profileManager.ActiveProfile;

        // Skills metadata
        var skillsMetadata = await skillService.GetSkillsMetadataAsync(cancellationToken);
        var skillsSection = profile.SendSkills
            ? skillService.FormatSkillsForSystemPrompt(skillsMetadata)
            : string.Empty;

        // Code context — solution structure only, never active file
        var contextSection = new StringBuilder();
        var currentContext = vsCodeContextService.CurrentContext;
        if (currentContext != null && profile.SendSolutionStructure && currentContext.SolutionFiles.Count > 0)
        {
            contextSection.AppendLine("# CURRENT CODE CONTEXT");
            contextSection.AppendLine($"""
                                      Solution structure:
                                      ```
                                      {BuildSolutionFiles(currentContext, true)}
                                      ```
                                      """);
        }

        // Rules
        var rules = profile.SendRules
            ? await ruleService.GetRulesAsync(cancellationToken)
            : string.Empty;

        // agents.md
        var agents = profile.SendAgentsMd
            ? await ruleService.GetAgentsMdAsync(cancellationToken)
            : string.Empty;

        // Mode instructions — always Agent mode for sub-agents
        var modeInstructions = profile.SendModeInstructions
            ? BuildModeInstructions(AppMode.Agent)
            : string.Empty;

        List<string?> systemPromptBlocks =
        [
            string.IsNullOrEmpty(customPrompt)
                ? "You are a helpful assistant. Complete the task given to you."
                : customPrompt,
            modeInstructions,
            skillsSection,
            contextSection.ToString(),
            rules,
            !string.IsNullOrEmpty(agents) ? string.Join("# Agents instructions\n", agents) : null,
            profile.SendCurrentDate ? $"Current date: {DateTime.Now:dd-MM-yyyy}" : null
        ];

        return string.Join(Environment.NewLine, systemPromptBlocks.Where(b => !string.IsNullOrEmpty(b)));
    }

    private static string BuildModeInstructions(AppMode mode)
    {
        var modeDesc = mode switch
        {
            AppMode.Agent => $"{mode} (for taking actions and applying changes)",
            AppMode.Plan => $"{mode} (for planning before taking actions)",
            _ => $"{mode} (for discussion, reading and explanations)",
        };

        var sb = new StringBuilder();
        sb.AppendLine($"Your current mode: {modeDesc}");

        if (mode == AppMode.Plan)
        {
            sb.AppendLine("""
                          ## Planning Mode Instructions
                          You are currently in **PLANNING MODE**. Your goal is to analyze the user's request, explore the codebase, and propose a detailed, step-by-step plan for implementation.
                          
                          1. **Analyze**: Use available tools to understand the current state of the project.
                          2. **Propose**: Create a structured plan. The plan should be realistic and broken down into logical steps.
                          3. **Format**: Wrap your final plan in `<plan>` tags. Each step should be clear and actionable.
                          
                          **Example:**
                          <plan>
                          1. Create a new service `StorageService`.
                          2. Register it in `Program.cs`.
                          3. Update `SettingsPage` to use the new service.
                          </plan>

                          In this mode, you should NOT make any changes to files. Your goal is to get user approval for the plan.
                          Once the plan is approved, the mode will be switched to **Agent** for execution.
                          """);
        }

        if (mode == AppMode.Agent)
        {
            sb.AppendLine("""
                          ## Sub-Agent Delegation
                          You can delegate tasks to sub-agents using the `delegate_task` tool. Sub-agents have their own conversation context and system prompt.
                          
                          - Use sub-agents for complex subtasks that benefit from focused attention and a specialized prompt.
                          - The sub-agent's final answer is returned to you as the tool result.
                          """);
        }

        return sb.ToString();
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
                    pathIndex = item.IndexOf(VsCodeContext.DirPrefix, StringComparison.Ordinal);
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
                        var dirPos = item.IndexOf(lastDir, StringComparison.Ordinal);
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
