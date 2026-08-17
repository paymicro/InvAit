namespace UIBlazor.Agents;

/// <summary>
/// То что не должно идти в VS а обрабатывается прямо тут
/// </summary>
public class InternalExecutor(IServiceProvider serviceProvider) : IInternalExecutor
{
    public async Task<VsToolResult> ExecuteToolAsync(string name, string argsJson, CancellationToken cancellationToken)
        => await ExecuteToolAsync(name, argsJson, null, cancellationToken);

    public async Task<VsToolResult> ExecuteToolAsync(string name, string argsJson, ToolCall? toolCall, CancellationToken cancellationToken)
    {
        if (name == BasicEnum.SwitchMode)
        {
            var args = JsonUtils.DeserializeParameters(argsJson);
            if (args != null && args.TryGetValue("mode", out var modeObj))
            {
                if (Enum.TryParse<AppMode>(modeObj.ToString(), true, out var mode))
                {
                    serviceProvider.GetRequiredService<IChatService>().Session.Mode = mode;
                    return new VsToolResult
                    {
                        Success = true,
                        Result = $"Switched to {mode} mode successfully. Now you have access to different set of tools."
                    };
                }
            }
            return new VsToolResult
            {
                Success = false,
                ErrorMessage = "Not supported mode"
            };
        }

        if (name == BasicEnum.AskUser)
        {
            return ExecuteAskUserAsync(argsJson);
        }

        if (name == BuiltInToolEnum.DelegateTask)
        {
            if (toolCall is null)
            {
                return new VsToolResult
                {
                    Success = false,
                    ErrorMessage = "delegate_task requires a tool call context."
                };
            }

            var subAgentExecutor = serviceProvider.GetRequiredService<ISubAgentExecutor>();
            return await subAgentExecutor.ExecuteAsync(argsJson, toolCall, cancellationToken);
        }

        return new VsToolResult
        {
            Success = false,
            ErrorMessage = $"Not supported tool {name}"
        };
    }

    /// <summary>
    /// Execute ask_user tool.
    /// Parses question and options from named JSON fields.
    /// Returns JSON with question and options for UI to render.
    /// </summary>
    private static VsToolResult ExecuteAskUserAsync(string argsJson)
    {
        var args = JsonUtils.DeserializeParameters(argsJson);
        var question = string.Empty;
        var options = new List<string>();

        if (args != null)
        {
            if (args.TryGetValue("question", out var questionObj))
            {
                question = questionObj?.ToString()?.Trim() ?? string.Empty;
            }

            if (args.TryGetValue("options", out var optionsObj) && optionsObj is JsonElement optionsEl)
            {
                if (optionsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in optionsEl.EnumerateArray())
                    {
                        var option = item.ValueKind == JsonValueKind.String
                            ? item.GetString()?.Trim()
                            : item.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(option))
                        {
                            options.Add(option);
                        }
                    }
                }
            }
            else if (optionsObj != null)
            {
                // Fallback: if options is a single string, treat as one option
                var option = optionsObj.ToString()?.Trim();
                if (!string.IsNullOrEmpty(option))
                {
                    options.Add(option);
                }
            }
        }

        var resultJson = JsonSerializer.Serialize(new { question, options });

        return new VsToolResult
        {
            Success = true,
            Result = resultJson
        };
    }
}
