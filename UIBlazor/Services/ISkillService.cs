namespace UIBlazor.Services;

public interface ISkillService
{
    /// <summary>
    /// Получить метаданные всех скиллов (только название + описание для системного промпта)
    /// </summary>
    Task<List<SkillMetadata>> GetSkillsMetadataAsync(CancellationToken cancellationToken);
    
    /// <summary>
    /// Загрузить полное содержимое скилла (только при активации)
    /// </summary>
    Task<VsToolResult> LoadSkillContentMarkDownAsync(IReadOnlyDictionary<string, object> args, CancellationToken cancellationToken);

    /// <summary>
    /// Форматировать метаданные скиллов для системного промпта
    /// </summary>
    string FormatSkillsForSystemPrompt(List<SkillMetadata> skills);

    /// <summary>
    /// Обновить кеш скиллов
    /// </summary>
    Task RefreshCacheAsync(CancellationToken cancellationToken);
}
