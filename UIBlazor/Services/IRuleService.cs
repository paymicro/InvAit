namespace UIBlazor.Services;

public interface IRuleService
{
    /// <summary>
    /// Gets the rules content from .agents/rules.md
    /// </summary>
    Task<string> GetRulesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the agents content from agents.md
    /// </summary>
    Task<string> GetAgentsMdAsync(CancellationToken cancellationToken);
}
