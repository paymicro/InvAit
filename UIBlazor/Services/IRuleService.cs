namespace UIBlazor.Services;

public interface IRuleService
{
    /// <summary>
    /// Gets the rules content from .agent/rules.md
    /// </summary>
    Task<string> GetRulesAsync();

    /// <summary>
    /// Refreshes the rules cache
    /// </summary>
    Task RefreshCacheAsync();
}
