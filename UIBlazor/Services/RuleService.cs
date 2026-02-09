using Shared.Contracts;

namespace UIBlazor.Services;

public class RuleService(IVsBridge vsBridge) : IRuleService
{
    private string? _rulesCache;
    private DateTime _lastCacheUpdate = DateTime.MinValue;

    /// <summary>
    /// Получить содержимое rules.md (кешируется)
    /// </summary>
    public async Task<string> GetRulesAsync()
    {
        // Проверяем кеш (обновляем раз в 2 минуты)
        if (_rulesCache != null && (DateTime.UtcNow - _lastCacheUpdate).TotalMinutes < 2)
        {
            return _rulesCache;
        }
        
        var result = await vsBridge.ExecuteToolAsync(BuiltInToolEnum.GetRules);
        if (!result.Success)
        {
            return _rulesCache ?? string.Empty;
        }
        
        _rulesCache = result.Result;
        _lastCacheUpdate = DateTime.UtcNow;
        return _rulesCache;
    }
    
    /// <summary>
    /// Принудительно обновить кеш правил
    /// </summary>
    public Task RefreshCacheAsync()
    {
        _lastCacheUpdate = DateTime.MinValue; // Сбрасываем кеш
        _rulesCache = null;
        return Task.CompletedTask;
    }
}
