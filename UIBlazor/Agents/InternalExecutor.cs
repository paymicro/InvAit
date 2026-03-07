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
        return new VsToolResult
        {
            Success = false,
            ErrorMessage = $"Not supported tool {name}"
        };
    }
}
