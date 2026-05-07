using InvAit.Utils;
using Microsoft.VisualStudio.Shell;
using ToolCore;

namespace InvAit.Agent;

/// <summary>
/// ILogger implementation that bridges to InvAit's Logger
/// </summary>
public class VsLogger : ILogger
{
    public void Log(string message, string level = "INFO")
    {
        Logger.LogAsync(message, level).FireAndForget();
    }
}
