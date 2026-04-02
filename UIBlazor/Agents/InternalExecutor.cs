using UIBlazor.Services;

namespace UIBlazor.Agents;

/// <summary>
/// То что не должно идти в VS а обрабатывается прямо тут
/// </summary>
public class InternalExecutor(IServiceProvider serviceProvider) : IInternalExecutor
{
    public async Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object> args, CancellationToken cancellationToken)
    {
        if (name == BasicEnum.SwitchMode)
        {
            if (args != null && args.TryGetValue("param1", out var modeObj))
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
            return ExecuteAskUserAsync(args);
        }

        return new VsToolResult
        {
            Success = false,
            ErrorMessage = $"Not supported tool {name}"
        };
    }

    /// <summary>
    /// Execute ask_user tool.
    /// param1 = question, param2+ = options
    /// Returns JSON with question and options for UI to render.
    /// </summary>
    private static VsToolResult ExecuteAskUserAsync(IReadOnlyDictionary<string, object> args)
    {
        var question = string.Empty;
        var options = new List<string>();

        if (args != null)
        {
            // Get param keys
            var paramKeys = args.Keys.ToList();

            // First param is the question
            if (paramKeys.Count > 0 && args.TryGetValue(paramKeys[0], out var questionObj))
            {
                question = questionObj?.ToString()?.Trim() ?? string.Empty;
            }

            // Remaining params are options
            for (var i = 1; i < paramKeys.Count; i++)
            {
                if (args.TryGetValue(paramKeys[i], out var optionObj))
                {
                    var option = optionObj?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(option))
                    {
                        options.Add(option);
                    }
                }
            }
        }

        // Return result with question and options as JSON
        var resultJson = JsonSerializer.Serialize(new { question, options });

        return new VsToolResult
        {
            Success = true,
            Result = resultJson
        };
    }
}
