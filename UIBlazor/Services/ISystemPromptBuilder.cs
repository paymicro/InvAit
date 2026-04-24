namespace UIBlazor.Services;

public interface ISystemPromptBuilder
{
    /// <summary>
    /// Asynchronously prepares the system prompt by combining configured instructions, tool usage guidance, skill
    /// metadata, and the current code context.
    /// </summary>
    Task<string> PrepareSystemPromptAsync(AppMode mode, CancellationToken cancellationToken);

    string BuildSolutionFiles(VsCodeContext currentContext, bool compress);
}
