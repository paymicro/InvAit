using System.Collections.Concurrent;
using System.Text.Json.Nodes;
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

    private List<Tool>? _mcpToolsCache;

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
            logger.LogError(ex, "Failed to save tool settings");
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

    private void McpSettingsProviderOnSaved()
    {
        _mcpToolsCache = null; // чистка кеша MCP тулзов при каждой загрузке из файла
    }

    protected override Task AfterInitAsync()
    {
        foreach (var tool in _registeredTools.Values)
        {
            tool.Enabled = !Current.DisabledTools.Contains(tool.Name);
        }

        mcpSettingsProvider.OnSaved += McpSettingsProviderOnSaved;
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
        if (_mcpToolsCache != null)
            return _mcpToolsCache;

        _mcpToolsCache = [.. BuildMcpTools()];
        return _mcpToolsCache;
    }

    public IEnumerable<Tool> BuildMcpTools()
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
                var currentToolConfig = toolConfig;
                var currentServer = server;
                yield return new Tool
                {
                    Name = toolName,
                    DisplayName = currentToolConfig.Name,
                    Description = currentToolConfig.Description ?? string.Empty,
                    Category = ToolCategory.Mcp,
                    Enabled = isEnabled,
                    ExampleToSystemMessage = BuildSchemaDescription(toolName, currentToolConfig),
                    ExecuteAsync = (args, cancellationToken) =>
                    {
                        var arguments = GetArgumentNamesFromSchema(currentToolConfig.InputSchema, args);
                        var mcpArgs = new Dictionary<string, object>
                        {
                            { "serverId", currentServer.Name },
                            { "toolName", currentToolConfig.Name },
                            { "arguments", arguments },
                            // Command/Arguments for auto-start if needed
                            { "command", currentServer.Command },
                            { "args", string.Join(" ", currentServer.Args) },
                            { "env", currentServer.Env }
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
            var parts = name.Split("__", 3, StringSplitOptions.None);
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
        // Use SchemaProcessor for complex schemas
        var schemaProperty = SchemaProcessor.DeserializeSchema(schemaElement);
        if (schemaProperty == null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var exampleObj = SchemaProcessor.GenerateExample(schemaProperty);

        sb.AppendLine("For example:");
        sb.AppendLine($"<function name=\"{toolName}\">");
        AppendExample(sb, exampleObj, indentLevel: 1); // Start indentation at 1 for the function body
        sb.AppendLine("</function>");

        sb.AppendLine("*Properties schema:*");
        AppendSchemaDescription(sb, schemaProperty, parentPath: "");

        return sb.ToString();
    }

    private static void AppendExample(StringBuilder sb, object obj, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);
        switch (obj)
        {
            case JsonObject jo:
                sb.AppendLine("{");
                foreach (var kvp in jo)
                {
                    sb.Append(indent).Append("  ").Append(kvp.Key).Append(" : ");
                    AppendExample(sb, kvp.Value, indentLevel + 1);
                }
                sb.AppendLine(indent).Append("}");
                break;
            case JsonArray ja:
                sb.AppendLine("[");
                foreach (var item in ja)
                {
                    sb.Append(indent).Append("  ");
                    AppendExample(sb, item, indentLevel + 1);
                }
                sb.AppendLine(indent).Append("]");
                break;
            default:
                sb.AppendLine(obj?.ToString() ?? "null");
                break;
        }
    }

    private static void AppendSchemaDescription(StringBuilder sb, JsonSchemaProperty prop, string parentPath, int depth = 0)
    {
        if (prop.Properties != null)
        {
            foreach (var nestedPropKvp in prop.Properties)
            {
                var currentPath = string.IsNullOrEmpty(parentPath) ? nestedPropKvp.Key : $"{parentPath}.{nestedPropKvp.Key}";
                var nestedProp = nestedPropKvp.Value;

                var typeInfo = nestedProp.Type ?? "unknown";
                if (nestedProp.EnumValues != null && nestedProp.EnumValues.Count > 0)
                {
                    typeInfo += $" (enum: {string.Join(", ", nestedProp.EnumValues.Select(v => v.ToString()))})";
                }
                if (nestedProp.Minimum.HasValue) typeInfo += $" (min: {nestedProp.Minimum.Value})";
                if (nestedProp.Maximum.HasValue) typeInfo += $" (max: {nestedProp.Maximum.Value})";
                if (nestedProp.MinLength.HasValue) typeInfo += $" (minLen: {nestedProp.MinLength.Value})";
                if (nestedProp.MaxLength.HasValue) typeInfo += $" (maxLen: {nestedProp.MaxLength.Value})";
                if (!string.IsNullOrEmpty(nestedProp.Pattern)) typeInfo += $" (pattern: {nestedProp.Pattern})";

                sb.AppendLine($"{currentPath} : [{typeInfo}] {nestedProp.Description ?? ""}");

                // Recurse for nested objects
                if (nestedProp.Type?.ToLowerInvariant() == "object")
                {
                    AppendSchemaDescription(sb, nestedProp, currentPath, depth + 1);
                }
            }
        }
        else if (prop.Type?.ToLowerInvariant() == "array" && prop.Items != null)
        {
            // Describe the array item type
            var itemTypeInfo = prop.Items.Type ?? "unknown";
            if (prop.Items.EnumValues != null && prop.Items.EnumValues.Count > 0)
            {
                itemTypeInfo += $" (enum: {string.Join(", ", prop.Items.EnumValues.Select(v => v.ToString()))})";
            }
            // ... potentially add minItems, maxItems if schema defines them ...
            sb.AppendLine($"{parentPath}[] : [array of {itemTypeInfo}] {prop.Description ?? ""}");

            // If the item itself is an object, recurse
            if (prop.Items.Type?.ToLowerInvariant() == "object")
            {
                var itemPath = $"{parentPath}[]_item"; // Represent the item placeholder
                AppendSchemaDescription(sb, prop.Items, itemPath, depth + 1);
            }
        }
    }

    // Uses SchemaProcessor for recursive handling
    private static Dictionary<string, object> GetArgumentNamesFromSchema(JsonElement? schemaElement, IReadOnlyDictionary<string, object> args)
    {
        var schemaProperty = SchemaProcessor.DeserializeSchema(schemaElement);
        if (schemaProperty == null)
        {
            return [];
        }

        // Use the new processor to validate and convert
        return SchemaProcessor.ValidateAndConvertArguments(schemaProperty, args);
    }

    // TODO поддержка только плоской схемы без вложенных объектов - this method also needs update potentially, or a new equivalent using SchemaProcessor
    public Dictionary<string, (string Name, string Desc, bool Required)> GetParameterNamesFromSchema(JsonElement? schemaElement)
    {
        // This method primarily serves the UI for simple flat parameter listing.
        // For complex nested schemas, the primary logic now resides in SchemaProcessor and is used by GetArgumentNamesFromSchema.
        // We could adapt this to return a hierarchical structure, but for now, keeping it simple might be sufficient for its current UI usage.
        // Let's keep the old logic for backward compatibility if needed, but mark it as legacy.

        // Legacy flat parsing
        if (!schemaElement.HasValue || schemaElement.Value.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new Dictionary<string, (string Name, string Desc, bool Required)>();
        var schema = schemaElement.Value;
        // Get required properties list
        var requiredProps = new HashSet<string>();
        if (schema.TryGetProperty("required", out var requiredArray) && requiredArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in requiredArray.EnumerateArray())
            {
                requiredProps.Add(req.GetString() ?? string.Empty);
            }
        }

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                var propType = prop.Value.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString() ?? string.Empty
                    : string.Empty;
                var desc = prop.Value.TryGetProperty("description", out var descElement)
                    ? descElement.GetString() ?? string.Empty
                    : string.Empty;
                var isRequired = requiredProps.Contains(prop.Name);
                result[prop.Name] = (propType, desc, isRequired);
            }
        }

        return result;
    }
}
