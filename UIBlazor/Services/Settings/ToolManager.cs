using System.Collections.Concurrent;
using System.Diagnostics;

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
                    state = new ToolModeSettings
                    {
                        IsEnabled = true,
                        ApprovalMode = ToolApprovalMode.AutoApprove
                    };
                    Current.CategoryStates[group.Key] = state;
                }
            }

            foreach (var tool in _registeredTools.Values)
            {
                Current.ToolStates[tool.Name] = tool.Enabled;
            }

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
            state = new ToolModeSettings();
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
            if (Current.ToolStates.TryGetValue(tool.Name, out var isEnabled))
            {
                tool.Enabled = isEnabled;
            }
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

        Current.ToolStates.Clear();
        return SaveAsync();
    }
    
    public IEnumerable<Tool> GetEnabledTools() => _registeredTools.Values.Where(t => 
    {
        if (Current.CategoryStates.TryGetValue(t.Category, out var state))
        {
            return state.IsEnabled && t.Enabled;
        }
        // по умолчанию категория включена
        return true;
    });

    public IEnumerable<Tool> GetAllTools() => _registeredTools.Values;

    public Tool? GetTool(string name)
    {
        return _registeredTools.TryGetValue(name, out var tool) ? tool : null;
    }

    public string GetToolUseSystemInstructions(AppMode mode)
    {
        var enabledTools = GetEnabledTools().ToList();
        
        // Filter tools based on mode
        enabledTools = mode switch
        {
            AppMode.Chat => [.. enabledTools.Where(t => t.Category is ToolCategory.ModeSwitch or ToolCategory.Browser)],
            AppMode.Agent => enabledTools,
            AppMode.Plan => [.. enabledTools.Where(t => t.Category is ToolCategory.ReadFiles or ToolCategory.ModeSwitch or ToolCategory.Browser)], // Placeholder for Plan mode
            _ => enabledTools
        };

        var sb = new StringBuilder();
        sb.AppendLine($"Current date: {DateTime.Now:f}");
        sb.AppendLine($"Current Application Mode: {mode}");
        sb.AppendLine("Available modes: Chat (for discussion, reading and explanations), Agent (for taking actions and applying changes), Plan (for planning).");
        sb.AppendLine("Use Mermaid diagrams for clarity in explanations. This will help you better visualize the answer formula.");
        sb.AppendLine($"You can use '{BuiltInToolEnum.SwitchMode}' tool to change current mode if you need more tools or want to switch context.");
        
        if (enabledTools.Count == 0)
        {
            return sb.ToString();
        }

        if (mode == AppMode.Agent)
        {
            sb.AppendLine("You are a function-calling agent.");
        }

        sb.AppendLine("""
                      ## Tool use instructions

                      You have access to several "tools" that you can use at any time to retrieve information and/or perform tasks for the User.

                      You MUST invoke tools exclusively with the following literal syntax:
                      <tool_call_begin> functions.<toolName>
                      Parameters
                      <tool_call_end>

                      Immediately after using any toll - stop generation, no explanatory text.

                      Explanation:
                        <tool_call_begin> functions.<toolName>            # function header. toolName - function name.
                        param1                                              # parameter 1
                        param2                                              # parameter 2
                        <tool_call_end>                                   # end of first call
                        <tool_call_begin> functions.<toolName>            # optional second function header. toolName - function name.
                        param1                                              # parameter 1 of second function.
                        <tool_call_end>                                   # end of second call

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

    public List<AiTool> ParseToolBlock(string content)
    {
        var result = new List<AiTool>();

        // Регулярное выражение адаптировано под гибридный формат:
        var callRegex = new Regex(
            @"<tool_call_begin>\s*functions\.(\w+)(?::(\d+))?\s*(.*?)\s*<tool_call_end>",
            RegexOptions.Singleline);

        foreach (Match callMatch in callRegex.Matches(content))
        {
            var toolName = callMatch.Groups[1].Value;
            var callId = callMatch.Groups[2].Success ? callMatch.Groups[2].Value : Guid.NewGuid().ToString();
            var args = callMatch.Groups[3].Value;
            var arguments = Parse(args);

            result.Add(new AiTool
            {
                Type = "function",
                Id = callId,
                Index = result.Count,
                Function = new AiToolToCall
                {
                    Name = toolName,
                    Arguments = arguments
                }
            });
        }

        return result;
    }

    private Dictionary<string, object> Parse(string input)
    {
        var result = new Dictionary<string, object>();
        var reader = new StringReader(input);
        string? line;
        var paramIndex = 0;
        var namedIndex = 0;

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

        return result;
    }
}
