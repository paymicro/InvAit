using UIBlazor.Services;

namespace UIBlazor.Agents;

/// <summary>
/// То что не должно идти в VS а обрабатывается прямо тут
/// </summary>
public class InternalExecutor(IServiceProvider serviceProvider) : IInternalExecutor
{
    public async Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object> args)
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
            return await ExecuteAskUserAsync(args);
        }

        return new VsToolResult
        {
            Success = false,
            ErrorMessage = $"Not supported tool {name}"
        };
    }

    private static Task<VsToolResult> ExecuteAskUserAsync(IReadOnlyDictionary<string, object> args)
    {
        string question = string.Empty;
        var options = new List<string>();

        if (args != null)
        {
            // Extract question
            if (args.TryGetValue("question", out var questionObj))
            {
                question = questionObj?.ToString() ?? string.Empty;
            }

            // Extract options - they come as param1, param2, etc. after "options" marker
            // The parser puts each line as a separate param
            var optionParams = args
                .Where(kv => kv.Key.StartsWith("param"))
                .OrderBy(kv => int.Parse(kv.Key[5..]))
                .Select(kv => kv.Value?.ToString() ?? string.Empty)
                .ToList();

            // Find where "options" marker is and take everything after it
            var optionsIndex = optionParams.IndexOf("options");
            if (optionsIndex >= 0 && optionsIndex + 1 < optionParams.Count)
            {
                // Check if next param after "options" is the question value (not another marker)
                // Format: param1 = question, param2 =<question text>, param3 = options, param4+ = actual options
                var questionIndex = optionParams.IndexOf("question");
                if (questionIndex >= 0 && questionIndex + 1< optionParams.Count)
                {
                    question = optionParams[questionIndex + 1];
                }

                // Options are after "options" marker
                for (var i = optionsIndex + 1; i < optionParams.Count; i++)
                {
                    var opt = optionParams[i].Trim();
                    if (!string.IsNullOrEmpty(opt) && opt != "question")
                    {
                        options.Add(opt);
                    }
                }
            }
            else
            {
                // Try alternate format: question is param after "question" marker
                var questionIndex = optionParams.IndexOf("question");
                if (questionIndex >= 0 && questionIndex + 1 < optionParams.Count)
                {
                    question = optionParams[questionIndex + 1];
                }

                // Options might be directly listed
                optionsIndex = optionParams.IndexOf("options");
                if (optionsIndex >= 0)
                {
                    for (var i = optionsIndex + 1; i < optionParams.Count; i++)
                    {
                        var opt = optionParams[i].Trim();
                        if (!string.IsNullOrEmpty(opt))
                        {
                            options.Add(opt);
                        }
                    }
                }
            }
        }

        // Return result with question and options as JSON
        var resultJson = JsonSerializer.Serialize(new { question, options });

        return Task.FromResult(new VsToolResult
        {
            Success = true,
            Result = resultJson
        });
    }
}
