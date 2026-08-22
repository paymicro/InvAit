namespace UIBlazor.Services.Interfaces;

public interface ISystemPromptBuilder
{
    /// <summary>
    /// Asynchronously prepares the system prompt by combining configured instructions, tool usage guidance, skill
    /// metadata, and the current code context.
    /// </summary>
    Task<string> PrepareSystemPromptAsync(AppMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// Builds a system prompt for a sub-agent.
    /// Combines the LLM-provided custom prompt with the same context sections as the main agent
    /// (rules, skills, solution structure, mode instructions, etc.), with exceptions:
    /// - Active file content is never included (sub-agent can read files via tools).
    /// - Mermaid diagram instructions are never included.
    /// </summary>
    /// <param name="customPrompt">The system prompt provided by the main agent via delegate_task.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string> PrepareSubAgentSystemPromptAsync(string customPrompt, CancellationToken cancellationToken);

    string BuildSolutionFiles(VsCodeContext currentContext, bool compress);
}
