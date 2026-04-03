namespace UIBlazor.Services;

public class RuleService(IVsBridge vsBridge) : IRuleService
{
    private string? _rulesCache;
    private DateTime _lastCacheUpdate = DateTime.MinValue;
    private string? _agentsCache;
    private DateTime _lastAgentsCacheUpdate = DateTime.MinValue;

    /// <summary>
    /// Получить содержимое rules.md (кешируется)
    /// </summary>
    public async Task<string> GetRulesAsync(CancellationToken cancellationToken)
    {
        // Проверяем кеш (обновляем раз в 2 минуты)
        if (_rulesCache != null && (DateTime.UtcNow - _lastCacheUpdate).TotalMinutes < 2)
        {
            return _rulesCache;
        }

        var result = await vsBridge.ExecuteToolAsync(BasicEnum.GetRules, cancellationToken: cancellationToken);
        if (!result.Success)
        {
            return _rulesCache ?? string.Empty;
        }

        _rulesCache = result.Result;
        _lastCacheUpdate = DateTime.UtcNow;
        return _rulesCache;
    }

    /// <summary>
    /// Получить содержимое agents.md (кешируется)
    /// </summary>
    public async Task<string> GetAgentsMdAsync(CancellationToken cancellationToken)
    {
        // Проверяем кеш (обновляем раз в 2 минуты)
        if (_agentsCache != null && (DateTime.UtcNow - _lastAgentsCacheUpdate).TotalMinutes < 2)
        {
            return _agentsCache;
        }

        var result = await vsBridge.ExecuteToolAsync(BasicEnum.GetAgents, cancellationToken: cancellationToken);
        if (!result.Success)
        {
            return _agentsCache ?? string.Empty;
        }

        _agentsCache = string.Join("## Agents.md\n", result.Result);
        _lastAgentsCacheUpdate = DateTime.UtcNow;
        return _agentsCache;
    }
}
