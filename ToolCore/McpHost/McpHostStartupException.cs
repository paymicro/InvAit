using System.IO;

namespace ToolCore.McpHost;

/// <summary>
/// Thrown when the McpHost process cannot be started. <see cref="UserMessage"/> is
/// safe to show in the UI, including installation instructions for a missing .NET runtime.
/// </summary>
public class McpHostStartupException : Exception
{
    public string UserMessage { get; }

    public McpHostStartupException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        UserMessage = userMessage;
    }
}
