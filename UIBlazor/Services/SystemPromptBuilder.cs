namespace UIBlazor.Services;

public class SystemPromptBuilder : ISystemPromptBuilder
{
    private readonly IProfileManager _profileManager;
    private readonly IToolManager _toolManager;
    private readonly ISkillService _skillService;
    private readonly IRuleService _ruleService;
    private readonly IContextService _vsCodeContextService;

    /// <summary>
    /// Cached formatted solution tree. Invalidated when <see cref="IContextService.OnContextChanged"/>
    /// fires, so the tree is only rebuilt when the IDE pushes new context (e.g. after file changes).
    /// </summary>
    private string? _cachedSolutionTree;

    public SystemPromptBuilder(
        IProfileManager profileManager,
        IToolManager toolManager,
        ISkillService skillService,
        IRuleService ruleService,
        IContextService vsCodeContextService)
    {
        _profileManager = profileManager;
        _toolManager = toolManager;
        _skillService = skillService;
        _ruleService = ruleService;
        _vsCodeContextService = vsCodeContextService;

        vsCodeContextService.OnContextChanged += InvalidateSolutionTreeCache;
    }

    private void InvalidateSolutionTreeCache() => _cachedSolutionTree = null;

    public ConnectionProfile Options => _profileManager.ActiveProfile;

    public async Task<string> PrepareSystemPromptAsync(AppMode mode, CancellationToken cancellationToken)
    {
        var profile = _profileManager.ActiveProfile;

        // Delegation instructions are only included if delegate_task is actually available
        var canDelegate = profile.SendModeInstructions &&
            _toolManager.GetEnabledTools(mode).Any(t => t.Name == BuiltInToolEnum.DelegateTask);

        return await BuildPromptAsync(
            new PromptSpec(
                BasePrompt: profile.SystemPrompt,
                Mode: mode,
                CanDelegate: canDelegate,
                IncludeActiveFile: true,
                UseMermaid: profile.UseMermaidDiagrams),
            cancellationToken);
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
        // Sub-agents always run in Agent mode and never get delegation instructions:
        // delegate_task is excluded from their tool set by SubAgentExecutor.
        const string fallbackPrompt = "You are a helpful assistant. Complete the task given to you.";

        return await BuildPromptAsync(
            new PromptSpec(
                BasePrompt: string.IsNullOrEmpty(customPrompt) ? fallbackPrompt : customPrompt,
                Mode: AppMode.Agent,
                CanDelegate: false,
                IncludeActiveFile: false,
                UseMermaid: false),
            cancellationToken);
    }

    /// <summary>
    /// Parameters controlling which sections <see cref="BuildPromptAsync"/> includes.
    /// </summary>
    private readonly record struct PromptSpec(
        string BasePrompt,
        AppMode Mode,
        bool CanDelegate,
        bool IncludeActiveFile,
        bool UseMermaid);

    /// <summary>
    /// Assembles a system prompt from shared sections (skills, code context, rules,
    /// agents.md, date) according to <paramref name="spec"/>. Single source of truth
    /// for both the main agent and sub-agent prompts.
    /// </summary>
    private async Task<string> BuildPromptAsync(PromptSpec spec, CancellationToken cancellationToken)
    {
        var profile = _profileManager.ActiveProfile;

        List<string?> systemPromptBlocks =
        [
            spec.BasePrompt,
            spec.UseMermaid ? MermaidSection : string.Empty,
            profile.SendModeInstructions ? BuildModeInstructions(spec.Mode, spec.CanDelegate) : string.Empty,
            await BuildSkillsSectionAsync(cancellationToken),
            BuildContextSection(spec.IncludeActiveFile),
            profile.SendRules ? await _ruleService.GetRulesAsync(cancellationToken) : null,
            await BuildAgentsMdSectionAsync(cancellationToken),
            profile.SendCurrentDate ? $"Current date: {DateTime.Now:dd-MM-yyyy}" : null
        ];

        return string.Join(Environment.NewLine, systemPromptBlocks.Where(b => !string.IsNullOrEmpty(b)));
    }

    /// <summary>
    /// Mermaid usage instructions (main agent only).
    /// </summary>
    private const string MermaidSection =
        "Use Mermaid diagrams for clarity in explanations. This will help you better visualize the answer formula. Don`t use \", {, }, (, ), [, ], in Mermaid node names.";

    /// <summary>
    /// Skills metadata section, or empty if disabled.
    /// </summary>
    private async Task<string> BuildSkillsSectionAsync(CancellationToken cancellationToken)
    {
        var profile = _profileManager.ActiveProfile;
        if (!profile.SendSkills)
            return string.Empty;

        var skillsMetadata = await _skillService.GetSkillsMetadataAsync(cancellationToken);
        return _skillService.FormatSkillsForSystemPrompt(skillsMetadata);
    }

    /// <summary>
    /// "# CURRENT CODE CONTEXT" section: solution structure and (optionally) the
    /// active file content. Returns empty when nothing should be included.
    /// </summary>
    private string BuildContextSection(bool includeActiveFile)
    {
        var profile = _profileManager.ActiveProfile;
        var currentContext = _vsCodeContextService.CurrentContext;
        if (currentContext == null)
            return string.Empty;

        var codeContext = new List<string>();
        if (profile.SendSolutionStructure && currentContext.SolutionFiles.Count > 0)
        {
            codeContext.Add($"""
                            Solution structure:
                            ```
                            {BuildSolutionFiles(currentContext)}
                            ```
                            """);
        }
        if (includeActiveFile && profile.SendCurrentFile && !string.IsNullOrEmpty(currentContext.ActiveFilePath))
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

        if (codeContext.Count == 0)
            return string.Empty;

        var contextSection = new StringBuilder();
        contextSection.AppendLine("# CURRENT CODE CONTEXT");
        foreach (var item in codeContext)
        {
            contextSection.AppendLine(item);
        }
        return contextSection.ToString();
    }

    /// <summary>
    /// agents.md contents under a header, or null if disabled/empty.
    /// Note: deliberately not using string.Join here — with two strings it binds to
    /// the non-generic params overload and silently returns the value unchanged.
    /// </summary>
    private async Task<string?> BuildAgentsMdSectionAsync(CancellationToken cancellationToken)
    {
        var profile = _profileManager.ActiveProfile;
        if (!profile.SendAgentsMd)
            return null;

        var agents = await _ruleService.GetAgentsMdAsync(cancellationToken);
        return string.IsNullOrEmpty(agents) ? null : $"# Agents instructions\n{agents}";
    }

    private static string BuildModeInstructions(AppMode mode, bool canDelegate = false)
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

        if (mode == AppMode.Agent && canDelegate)
        {
            sb.AppendLine("""
                          ## Sub-Agent Delegation
                          You can delegate tasks to sub-agents using the `delegate_task` tool. Sub-agents have their own conversation context and system prompt.
                          
                          - Use sub-agents for complex subtasks that benefit from focused attention and a specialized prompt.
                          - The sub-agent's final answer is returned to you as the tool result.
                          - Sub-agents cannot delegate further (no recursion).
                          """);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a formatted solution tree from raw file paths in <paramref name="currentContext"/>.
    /// Uses <see cref="SolutionTreeBuilder"/> to construct the tree and
    /// <see cref="SolutionTreeFormatter"/> to render it as ASCII art.
    /// The result is cached and invalidated when the IDE pushes new context.
    /// </summary>
    public string BuildSolutionFiles(VsContext currentContext)
    {
        if (_cachedSolutionTree != null)
            return _cachedSolutionTree;

        var tree = SolutionTreeBuilder.Build(
            currentContext.SolutionFiles,
            currentContext.SolutionProjects,
            currentContext.SolutionPath);
        _cachedSolutionTree = SolutionTreeFormatter.Format(tree);
        return _cachedSolutionTree;
    }
}
