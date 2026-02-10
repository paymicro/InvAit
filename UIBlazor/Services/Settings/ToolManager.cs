using System.Collections.Concurrent;
using System.Diagnostics;
using UIBlazor.Components.Chat;

namespace UIBlazor.Services.Settings;

public class ToolManager(BuiltInAgent builtInAgent, ILocalStorageService localStorage)
    : BaseSettingsProvider<ToolSettings>(localStorage, "ToolSettings"), IToolManager
{
    private readonly ConcurrentDictionary<string, Tool> _registeredTools = new();

    public override async Task SaveAsync()
    {
        try
        {
            foreach (var group in _registeredTools.Values.GroupBy(t => t.Category))
            {
                if (!Current.CategoryStates.TryGetValue(group.Key, out var state))
                {
                    state = new ToolCategorySettings
                    {
                        IsEnabled = true,
                        ApprovalMode = ToolApprovalMode.AutoApprove
                    };
                    Current.CategoryStates[group.Key] = state;
                }
            }

            Current.DisabledTools = [.. _registeredTools.Values.Where(t => t.Enabled).Select(t => t.Name)];

            await base.SaveAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save tool settings: {ex.Message}");
        }
    }

    public void UpdateCategorySettings(ToolCategory category, bool isEnabled, ToolApprovalMode approvalMode)
    {
        if (!Current.CategoryStates.TryGetValue(category, out var state))
        {
            state = new ToolCategorySettings();
            Current.CategoryStates[category] = state;
        }
        state.IsEnabled = isEnabled;
        state.ApprovalMode = approvalMode;
        _ = SaveAsync();
    }

    public void ToggleTool(string toolName, bool isEnabled)
    {
        if (_registeredTools.TryGetValue(toolName, out var tool))
        {
            tool.Enabled = isEnabled;
            _ = SaveAsync();
        }
    }

    public void RegisterAllTools()
    {
        foreach (var tool in builtInAgent.Tools)
        {
            _registeredTools[tool.Name] = tool;
        }

        // Load tool settings after registration
        _ = InitializeAsync();
    }

    protected override void OnInitialized()
    {
        foreach (var tool in _registeredTools.Values)
        {
            tool.Enabled = !Current.DisabledTools.Contains(tool.Name);
        }
    }

    public async Task SaveToolSettingsAsync()
    {
        Debouncer.Trigger();
        await Task.CompletedTask;
    }

    public override Task ResetAsync()
    {
        foreach (var tool in _registeredTools.Values)
        {
            tool.Enabled = true;
        }

        foreach (var state in Current.CategoryStates.Values)
        {
            state.IsEnabled = true;
            state.ApprovalMode = ToolApprovalMode.AutoApprove;
        }

        Current.DisabledTools.Clear();
        return SaveAsync();
    }
    
    public IEnumerable<Tool> GetEnabledTools() => _registeredTools.Values.Where(t => 
    {
        if (Current.CategoryStates.TryGetValue(t.Category, out var state))
        {
            return state.IsEnabled && t.Enabled;
        }
        // по умолчанию категория включена
        return t.Enabled;
    });

    public IEnumerable<Tool> GetAllTools() => _registeredTools.Values;

    public Tool? GetTool(string name)
    {
        return _registeredTools.TryGetValue(name, out var tool) ? tool : null;
    }

    public ToolApprovalMode GetApprovalModeByToolName(string name)
    {
        var tool = GetTool(name);
        return tool != null && Current.CategoryStates.TryGetValue(tool.Category, out var state)
            ? state.ApprovalMode
            : ToolApprovalMode.AutoApprove;
    }

    public string GetToolUseSystemInstructions(AppMode mode)
    {
        var enabledTools = GetEnabledTools().ToList();
        
        // Filter tools based on mode
        enabledTools = mode switch
        {
            AppMode.Chat => [.. enabledTools.Where(t => t.Category is ToolCategory.ModeSwitch or ToolCategory.Browser)],
            AppMode.Agent => enabledTools,
            AppMode.Plan => [.. enabledTools.Where(t => t.Category is ToolCategory.ReadFiles or ToolCategory.ModeSwitch or ToolCategory.Browser or ToolCategory.Mcp)],
            _ => enabledTools
        };

        var sb = new StringBuilder();
        sb.AppendLine($"Current date: {DateTime.Now:f}");
        sb.AppendLine($"Current Application Mode: {mode}");
        sb.AppendLine("Available modes: Chat (for discussion, reading and explanations), Agent (for taking actions and applying changes), Plan (for planning).");
        sb.AppendLine("Use Mermaid diagrams for clarity in explanations. This will help you better visualize the answer formula.");

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
        
        if (enabledTools.Any(t => t.Name == BuiltInToolEnum.SwitchMode))
        {
            sb.AppendLine($"You can use '{BuiltInToolEnum.SwitchMode}' tool to change current mode if you need more tools or want to switch context.");
        }
        
        if (enabledTools.Count == 0)
        {
            return sb.ToString();
        }

        if (mode == AppMode.Agent)
        {
            sb.AppendLine("You are a function-calling agent. You should take actions to fulfill the user's request.");
        }

        sb.AppendLine("""
                      ## Tool use instructions

                      You have access to several "tools" that you can use at any time to retrieve information and/or perform tasks for the User.

                      You MUST invoke tools exclusively with the following literal syntax:
                      <function name="<toolName>">
                      Parameters
                      </function>

                      Immediately after using any toll - stop generation, no explanatory text.

                      Explanation:
                        <function name="<toolName>">      # function header. toolName - function name.
                        param1                           # parameter 1
                        param2                           # parameter 2
                        </function>                      # end of first call
                        <function name="<toolName>">      # optional second function header. toolName - function name.
                        param1                           # parameter 1 of second function.
                        </function>                      # end of second call

                      The following tools/functions are available to you:

                      """);

        foreach (var tool in enabledTools)
        {
            sb.AppendLine("====");
            sb.AppendLine($"### {tool.Name}");
            sb.AppendLine(tool.Description);
            sb.AppendLine(tool.ExampleToSystemMessage);
            sb.AppendLine();
        }

        sb.AppendLine("""

                      If it seems like the User's request could be solved with the tools, choose the BEST tool for the job based on the user's request and the tool descriptions
                      Then send the tool_calls_section (YOU call the tool, not the user).
                      Do not perform actions with/for hypothetical files. Use tools to deduce which files are relevant.
                      You can call multiple tools in one tool_calls_section.
                      """);
        return sb.ToString();
    }

    public List<(string ToolName, string CallId, string Args)> ParseToolBlockRaw(string content)
    {
        var result = new List<(string toolName, string callId, string args)>();

        if (string.IsNullOrEmpty(content))
            return result;

        var callRegex = new Regex(
            @"<function name=""(\w+)""(?::(\d+))?>\s*(.*?)\s*</function>",
            RegexOptions.Singleline);

        foreach (Match callMatch in callRegex.Matches(content))
        {
            var toolName = callMatch.Groups[1].Value;
            var callId = callMatch.Groups[2].Success ? callMatch.Groups[2].Value : $"idx_{result.Count}";
            var args = callMatch.Groups[3].Value;
            result.Add((toolName, callId, args));
        }

        return result;
    }

    public IEnumerable<AiTool> ParseToolBlock(List<ContentSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (segment.Type == SegmentType.Tool && !string.IsNullOrEmpty(segment.ToolName))
            {
                var content = string.Join("\n", segment.Lines);
                var arguments = Parse(segment.ToolName, content);
                yield return new AiTool
                {
                    Type = "function",
                    Id = segment.Id,
                    Index = 0,
                    Function = new AiToolToCall
                    {
                        Name = segment.ToolName,
                        Arguments = arguments
                    }
                };
            }
        }
    }

    public List<AiTool> ParseToolBlock(string content)
    {
        var rawResults = ParseToolBlockRaw(content);
        var result = new List<AiTool>();

        if (rawResults.Count == 0)
            return result;

        for (var i = 0; i < rawResults.Count; i++)
        {
            var raw = rawResults[i];
            var arguments = Parse(raw.ToolName, raw.Args);
            result.Add(new AiTool
            {
                Type = "function",
                Id = raw.CallId,
                Index = i,
                Function = new AiToolToCall
                {
                    Name = raw.ToolName,
                    Arguments = arguments
                }
            });
        }

        return result;
    }

    public Dictionary<string, object> Parse(string toolName, List<string> toolLines)
    {
        var result = new Dictionary<string, object>();
        var paramIndex = 0;
        var namedIndex = 0;

        if (toolName == BuiltInToolEnum.ReadFiles)
        {
            ReadFileParams? fileParams = null;

            for (var i = 0; i < toolLines.Count; i++)
            {
                var line = toolLines[i];
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                    continue;

                if (trimmedLine == "start_line")
                {
                    var valLine = toolLines[++i]?.Trim();
                    if (fileParams != null && int.TryParse(valLine, out var startLine))
                    {
                        fileParams.StartLine = startLine;
                    }
                }
                else if (trimmedLine == "line_count")
                {
                    var valLine = toolLines[++i]?.Trim();
                    if (fileParams != null && int.TryParse(valLine, out var lineCount))
                    {
                        fileParams.LineCount = lineCount;
                    }
                }
                else
                {
                    fileParams = new ReadFileParams
                    {
                        Name = trimmedLine,
                        StartLine = -1,
                        LineCount = -1
                    };
                    result[$"file{++paramIndex}"] = fileParams;
                }
            }
        }
        else if (toolName == BuiltInToolEnum.ApplyDiff)
        {
            for (var i = 0; i < toolLines.Count; i++)
            {
                var line = toolLines[i];
                var trimmedLine = line.Trim();

                if (string.IsNullOrEmpty(trimmedLine))
                    continue;

                // Начало блока (<<<<<<< SEARCH)
                if (trimmedLine.StartsWith("<<<<<<< SEARCH"))
                {
                    var diff = new DiffReplacement();
                    var lastResult = result.LastOrDefault().Value?.ToString() ?? string.Empty;
                    if (lastResult.StartsWith(":start_line:"))
                    {
                        diff.StartLine = int.Parse(lastResult.Split(':')[2]);
                        result.Remove($"param{paramIndex}");
                    }
                    var search = new List<string>();
                    for (; i < toolLines.Count; i++)
                    {
                        line = toolLines[i];
                        if (line.Trim().StartsWith("=======")) break;
                        search.Add(line);
                    }
                    diff.Search = search;

                    var replace = new List<string>();
                    for (; i < toolLines.Count; i++)
                    {
                        line = toolLines[i];
                        if (line.Trim().StartsWith(">>>>>>> REPLACE")) break;
                        replace.Add(line);
                    }
                    diff.Replace = replace;

                    result[$"diff{++namedIndex}"] = diff;
                }
                // Обычная строка параметров
                else
                {
                    result[$"param{++paramIndex}"] = line;
                }
            }
        }
        else // обычные тулзы
        {
            for (var i = 0; i < toolLines.Count; i++)
            {
                var line = toolLines[i];
                var trimmedLine = line.Trim();

                if (string.IsNullOrEmpty(trimmedLine))
                    continue;

                result[$"param{++paramIndex}"] = line;
            }
        }

        return result;
    }

    private Dictionary<string, object> Parse(string toolName, string input)
    {
        var result = new Dictionary<string, object>();
        var reader = new StringReader(input);
        string? line;
        var paramIndex = 0;
        var namedIndex = 0;

        if (toolName == BuiltInToolEnum.ReadFiles)
        {
            ReadFileParams? fileParams = null;

            while ((line = reader.ReadLine()) != null)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                    continue;

                if (trimmedLine == "start_line")
                {
                    var valLine = reader.ReadLine()?.Trim();
                    if (fileParams != null && int.TryParse(valLine, out var startLine))
                    {
                        fileParams.StartLine = startLine;
                    }
                }
                else if (trimmedLine == "line_count")
                {
                    var valLine = reader.ReadLine()?.Trim();
                    if (fileParams != null && int.TryParse(valLine, out var lineCount))
                    {
                        fileParams.LineCount = lineCount;
                    }
                }
                else
                {
                    fileParams = new ReadFileParams
                    {
                        Name = trimmedLine,
                        StartLine = -1,
                        LineCount = -1
                    };
                    result[$"file{++paramIndex}"] = fileParams;
                }
            }
        }
        else if (toolName == BuiltInToolEnum.ApplyDiff)
        {
            while ((line = reader.ReadLine()) != null)
            {
                var trimmedLine = line.Trim();

                if (string.IsNullOrEmpty(trimmedLine))
                    continue;

                // Начало блока (<<<<<<< SEARCH)
                if (trimmedLine.StartsWith("<<<<<<< SEARCH"))
                {
                    var diff = new DiffReplacement();
                    var lastResult = result.LastOrDefault().Value?.ToString() ?? string.Empty;
                    if (lastResult.StartsWith(":start_line:"))
                    {
                        diff.StartLine = int.Parse(lastResult.Split(':')[2]);
                        result.Remove($"param{paramIndex}");
                    }
                    var search = new List<string>();
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().StartsWith("=======")) break;
                        search.Add(line);
                    }
                    diff.Search = search;

                    var replace = new List<string>();
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().StartsWith(">>>>>>> REPLACE")) break;
                        replace.Add(line);
                    }
                    diff.Replace = replace;

                    result[$"diff{++namedIndex}"] = diff;
                }
                // Обычная строка параметров
                else
                {
                    result[$"param{++paramIndex}"] = line;
                }
            }
        }
        else // обычные тулзы
        {
            while ((line = reader.ReadLine()) != null)
            {
                var trimmedLine = line.Trim();

                if (string.IsNullOrEmpty(trimmedLine))
                    continue;

                result[$"param{++paramIndex}"] = line;
            }
        }

        return result;
    }
}
