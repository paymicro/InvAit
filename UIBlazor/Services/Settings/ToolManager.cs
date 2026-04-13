using System.Collections.Concurrent;
using System.Diagnostics;
using Shared.Contracts.Mcp;

namespace UIBlazor.Services.Settings;

public class ToolManager(
    BuiltInAgent builtInAgent,
    ILogger<ToolManager> logger,
    ILocalStorageService localStorage,
    IMcpSettingsProvider mcpSettingsProvider,
    IVsBridge vsBridge)
    : BaseSettingsProvider<ToolSettings>(localStorage, logger, "ToolSettings"), IToolManager
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
                        ApprovalMode = ToolApprovalMode.Allow
                    };
                    Current.CategoryStates[group.Key] = state;
                }
            }

            Current.DisabledTools = [.. _registeredTools.Values.Where(t => !t.Enabled).Select(t => t.Name)];

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

    protected override Task AfterInitAsync()
    {
        foreach (var tool in _registeredTools.Values)
        {
            tool.Enabled = !Current.DisabledTools.Contains(tool.Name);
        }
        return Task.CompletedTask;
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
            state.ApprovalMode = ToolApprovalMode.Allow;
        }

        Current.DisabledTools.Clear();
        return SaveAsync();
    }

    public IEnumerable<Tool> GetEnabledTools()
    {
        var builtIn = _registeredTools.Values.Where(t =>
        {
            if (Current.CategoryStates.TryGetValue(t.Category, out var state))
            {
                return state.IsEnabled && t.Enabled;
            }
            return t.Enabled;
        });

        var mcp = GetMcpTools().Where(t => t.Enabled);

        return builtIn.Concat(mcp);
    }

    public IEnumerable<Tool> GetAllTools()
    {
        return _registeredTools.Values.Concat(GetMcpTools());
    }

    public Tool? GetTool(string name)
    {
        if (_registeredTools.TryGetValue(name, out var tool))
            return tool;

        return GetMcpTools().FirstOrDefault(t => t.Name == name);
    }

    public IEnumerable<Tool> GetMcpTools()
    {
        if (!mcpSettingsProvider.Current.Enabled)
        {
            yield break;
        }

        // перебираем все MCP сервера
        foreach (var server in mcpSettingsProvider.Current.Servers.Where(s =>
            mcpSettingsProvider.Current.ServerEnabledStates.TryGetValue(s.Name, out var serverEnabled)
                ? serverEnabled
                : s.Enabled))
        {
            // перебор всех тулзов в MCP этом сервере
            foreach (var toolConfig in server.Tools)
            {
                var toolName = $"mcp__{server.Name}__{toolConfig.Name}";

                var isEnabled = !mcpSettingsProvider.Current.ToolDisabledStates.Contains(toolName);

                yield return new Tool
                {
                    Name = toolName,
                    DisplayName = toolConfig.Name,
                    Description = toolConfig.Description ?? string.Empty,
                    Category = ToolCategory.Mcp,
                    Enabled = isEnabled,
                    ExampleToSystemMessage = BuildSchemaDescription(toolName, toolConfig),
                    ExecuteAsync = (args, cancellationToken) =>
                    {
                        var arguments = GetArgumentNamesFromSchema(toolConfig.InputSchema, args);
                        var mcpArgs = new Dictionary<string, object>
                        {
                            { "serverId", server.Name },
                            { "toolName", toolConfig.Name },
                            { "arguments", arguments },
                            // Command/Arguments for auto-start if needed
                            { "command", server.Command },
                            { "args", string.Join(" ", server.Args) },
                            { "env", server.Env }
                        };

                        return vsBridge.ExecuteToolAsync(BasicEnum.McpCallTool, mcpArgs, cancellationToken);
                    }
                };
            }
        }
    }

    public ToolApprovalMode GetApprovalModeByToolName(string name)
    {
        if (name.StartsWith("mcp__"))
        {
            var parts = name.Split("__"); // TODO есть риск что сервер в названии содержит __ и тогда он всегда будет AutoApprove
            if (parts.Length >= 2)
            {
                var serverName = parts[1];
                if (mcpSettingsProvider.Current.ServerApprovalModes.TryGetValue(serverName, out var mode))
                {
                    return mode;
                }
            }
            return ToolApprovalMode.Allow;
        }

        var tool = GetTool(name);
        return tool != null && Current.CategoryStates.TryGetValue(tool.Category, out var state)
            ? state.ApprovalMode
            : ToolApprovalMode.Allow;
    }

    private string GetModeDesc(AppMode mode)
        => mode switch
        {
            AppMode.Agent => $"{mode} (for taking actions and applying changes)",
            AppMode.Plan => $"{mode} (for planning before taking actions)",
            _ => $"{mode} (for discussion, reading and explanations)",
        };

    public string GetToolUseSystemInstructions(AppMode mode, bool hasSkills)
    {
        var enabledTools = GetEnabledTools().ToList();

        if (!hasSkills) // если нет скиллов, то не нужно их читать
        {
            enabledTools = [.. enabledTools.Where(t => t.Name != BasicEnum.ReadSkillContent)];
        }

        // Filter tools based on mode
        enabledTools = mode switch
        {
            AppMode.Chat => [.. enabledTools.Where(t => t.Category is ToolCategory.ReadFiles or ToolCategory.ModeSwitch or ToolCategory.Mcp)],
            AppMode.Agent => enabledTools,
            AppMode.Plan => [.. enabledTools.Where(t => t.Category is ToolCategory.ReadFiles or ToolCategory.ModeSwitch or ToolCategory.Mcp)],
            _ => enabledTools
        };

        var otherModes = string.Join(", ", Enum.GetValues<AppMode>().Where(m => m != mode).Select(m => GetModeDesc(m)));

        var sb = new StringBuilder();
        sb.AppendLine($"Current date: {DateTime.Now:f}");
        sb.AppendLine($"Your current mode: {GetModeDesc(mode)}");
        if (enabledTools.FirstOrDefault(t => t.Category == ToolCategory.ModeSwitch)?.Enabled == true)
        {
            sb.AppendLine($"Other available modes: {otherModes}.");
        }
        sb.AppendLine("Use Mermaid diagrams for clarity in explanations. This will help you better visualize the answer formula. Don`t use \", {, }, (, ), [, ], in Mermaid node names.");

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

        if (enabledTools.Any(t => t.Name == BasicEnum.SwitchMode))
        {
            sb.AppendLine($"You can use '{BasicEnum.SwitchMode}' tool to change current mode if you need more tools or want to switch context.");
        }

        if (enabledTools.Count == 0)
        {
            return sb.ToString();
        }

        if (mode == AppMode.Agent)
        {
            sb.AppendLine("You are a tool-calling agent. You should take actions to fulfill the user's request.");
            sb.AppendLine();
        }

        if (enabledTools.Count > 0)
        {
            sb.AppendLine("""
                      ## Tool use instructions

                      You have access to several tools/functions that you can use at any time to retrieve information and/or perform tasks for the User.

                      ## Execution Rules

                      **Multi-call:** You SHOULD invoke multiple tools within a single message if the task requires it. Do not limit yourself to one tool per response.

                      **Prioritize Examples:** Each tool has a specific usage example below. Always follow the tool's specific example and parameter format, as requirements vary between tools.
                      
                      **Syntax:** You MUST invoke tools exclusively with the following literal syntax:

                            <function name="toolName">
                            Parameters
                            </function>

                      **Constraints:**
                            - Use <function> tags ONLY for actual tool calls.
                            - Stop generation immediately after the last tool call.
                            - No conversational filler or explanations after tools.

                      The following tools/functions are available to you:

                      """);

            foreach (var tool in enabledTools)
            {
                sb.AppendLine("---");
                sb.AppendLine($"### Tool: {tool.Name}");
                sb.AppendLine($"**Description:** {tool.Description}");
                sb.AppendLine("**Calling:**");
                sb.AppendLine(tool.ExampleToSystemMessage);
                sb.AppendLine();
            }

            sb.AppendLine("""

                          If it seems like the User's request could be solved with the tools, choose the BEST tool for the job based on the user's request and the tool descriptions
                          Do not perform actions with/for hypothetical files. Use tools to deduce which files are relevant.
                          You can call multiple tools once.
                          """);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build a readable schema description for LLM prompt
    /// </summary>
    private static string BuildSchemaDescription(string toolName, McpToolConfig toolConfig)
    {
        var schemaElement = toolConfig.InputSchema;
        var requiredArgs = toolConfig.RequiredArguments;
        if (!schemaElement.HasValue || schemaElement.Value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var schema = schemaElement.Value;

        sb.AppendLine("For example:");
        sb.AppendLine($"<function name=\"{toolName}\">");

        var propDesc = new List<string>();
        // Get properties
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                var isRequired = requiredArgs.Contains(prop.Name);
                var type = "string";
                var description = string.Empty;

                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("type", out var typeProp))
                    {
                        type = typeProp.GetString() ?? "string";
                    }
                    if (prop.Value.TryGetProperty("description", out var descProp))
                    {
                        description = descProp.GetString() ?? string.Empty;
                    }
                }

                // TODO еще enum (допустимые значения), minimum, maximum (для чисел), minLength, maxLength, pattern (для сторок)
                var requiredMark = isRequired ? " (required)" : "";
                propDesc.Add($"{prop.Name} : [{type}]{requiredMark} {description}");

                sb.AppendLine($"{prop.Name} : {GetSampleByType(type)}");
            }
        }

        sb.AppendLine("</function>");

        if (propDesc.Count > 0)
        {
            sb.AppendLine("*Properties schema:*");
            sb.AppendLine(string.Join(Environment.NewLine, propDesc));
        }

        return sb.ToString();
    }

    private static string GetSampleByType(string propType)
    {
        return propType switch
        {
            "number" or "integer" => "123456",
            "boolean" => "true",
            "array" => "[]", // TODO нужно сделать поддержку массивов
            "object" => "object", // TODO вложенный объект...
            _ => "\"string\"",
        };
    }

    private static object GetArgumentByType(string propType, object arg)
    {
        // TODO array и object и enum
        return propType switch
        {
            "integer" => int.TryParse(arg.ToString(), out var valueInt) ? valueInt : arg,
            "number" => double.TryParse(arg.ToString(), out var valueDouble) ? valueDouble : arg,
            "boolean" => bool.TryParse(arg.ToString(), out var valueBool) ? valueBool : arg,
            _ => arg
        };
    }

    // TODO поддержка только плоской схемы без вложенных объектов
    private static Dictionary<string, object> GetArgumentNamesFromSchema(JsonElement? schemaElement, IReadOnlyDictionary<string, object> args)
    {
        if (!schemaElement.HasValue || schemaElement.Value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new Dictionary<string, object>();
        var schema = schemaElement.Value;
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (args.TryGetValue(prop.Name, out var arg))
                {
                    var propType = prop.Value.GetProperty("type").GetString() ?? string.Empty; // TODO этого не может быть - ошибку надо выкидывать.
                    result[prop.Name] = GetArgumentByType(propType, arg);
                }
            }
        }

        return result;
    }

    // TODO поддержка только плоской схемы без вложенных объектов
    public Dictionary<string, (string Name, string Desc)> GetParameterNamesFromSchema(JsonElement? schemaElement)
    {
        if (!schemaElement.HasValue || schemaElement.Value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new Dictionary<string, (string Name, string Desc)>();
        var schema = schemaElement.Value;
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                var propType = prop.Value.GetProperty("type").GetString() ?? string.Empty;
                var desc = prop.Value.GetProperty("description").GetString();
                result[prop.Name] = (propType, desc);
            }
        }

        return result;
    }
}
