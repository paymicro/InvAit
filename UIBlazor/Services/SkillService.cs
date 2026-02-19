namespace UIBlazor.Services;

public class SkillService(IVsBridge vsBridge) : ISkillService
{
    private List<SkillMetadata>? _skillsCache;
    private readonly Dictionary<string, SkillContent> _contentCache = new();
    private DateTime _lastCacheUpdate = DateTime.MinValue;

    /// <summary>
    /// Получить метаданные всех скиллов (кешируется)
    /// Вызывается при старте и при изменении файлов
    /// </summary>
    public async Task<List<SkillMetadata>> GetSkillsMetadataAsync()
    {
        // Проверяем кеш (обновляем раз в 2 минуты или по запросу)
        if (_skillsCache != null && (DateTime.UtcNow - _lastCacheUpdate).TotalMinutes < 2)
        {
            return _skillsCache;
        }

        var result = await vsBridge.ExecuteToolAsync(BasicEnum.GetSkillsMetadata);
        if (!result.Success)
        {
            return _skillsCache ?? new List<SkillMetadata>();
        }

        try
        {
            var metadataJson = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(result.Result);
            _skillsCache = metadataJson?.Select(m => new SkillMetadata
            {
                Name = m.GetValueOrDefault("name", ""),
                Description = m.GetValueOrDefault("description", ""),
                FilePath = m.GetValueOrDefault("filePath", "")
            }).ToList() ?? new List<SkillMetadata>();

            _lastCacheUpdate = DateTime.UtcNow;
            return _skillsCache;
        }
        catch
        {
            return _skillsCache ?? new List<SkillMetadata>();
        }
    }

    /// <summary>
    /// Загрузить полное содержимое скилла (с кешированием)
    /// Вызывается только когда агент активирует скилл
    /// </summary>
    public async Task<SkillContent?> LoadSkillContentAsync(string filePath)
    {
        // Проверяем кеш содержимого
        if (_contentCache.TryGetValue(filePath, out var cachedContent))
        {
            return cachedContent;
        }

        var args = new Dictionary<string, object>
        {
            { "param1", filePath }
        };

        var result = await vsBridge.ExecuteToolAsync(BasicEnum.ReadSkillContent, args);
        if (!result.Success)
        {
            return null;
        }

        try
        {
            var contentJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.Result);
            if (contentJson == null) return null;

            var skillContent = new SkillContent
            {
                Name = contentJson.GetValueOrDefault("name", default).GetString() ?? "",
                Description = contentJson.GetValueOrDefault("description", default).GetString() ?? "",
                Content = contentJson.GetValueOrDefault("content", default).GetString() ?? "",
                Resources = contentJson.GetValueOrDefault("resources", default)
                    .EnumerateArray()
                    .Select(r => r.GetString() ?? "")
                    .Where(r => !string.IsNullOrEmpty(r))
                    .ToList()
            };

            // Кешируем содержимое (максимум 10 скиллов в кеше)
            if (_contentCache.Count >= 10)
            {
                var oldestKey = _contentCache.Keys.First();
                _contentCache.Remove(oldestKey);
            }
            _contentCache[filePath] = skillContent;

            return skillContent;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Форматирует список скиллов для добавления в системный промпт
    /// Только название и описание (триггеры для активации)
    /// </summary>
    public string FormatSkillsForSystemPrompt(List<SkillMetadata> skills)
    {
        if (skills == null || skills.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Available Skills");
        sb.AppendLine();
        sb.AppendLine("You have access to the following skills. Skills are specialized instructions that you can activate by requesting them when relevant:");
        sb.AppendLine();

        foreach (var skill in skills)
        {
            sb.AppendLine($"");
            sb.AppendLine($"""
                           - **{skill.Name}**: {skill.Description}
                           Activate with: `<function name="{BasicEnum.ReadSkillContent}">
                                           {skill.FilePath}
                                           </function>`
                           """);
            sb.AppendLine();
        }

        sb.AppendLine($"When you need detailed instructions from a skill, use `{BasicEnum.ReadSkillContent}` tool to load it.");

        return sb.ToString();
    }

    /// <summary>
    /// Принудительно обновить кеш скиллов
    /// При изменении файлов (через FileSystemWatcher)
    /// </summary>
    public async Task RefreshCacheAsync()
    {
        _lastCacheUpdate = DateTime.MinValue; // Сбрасываем кеш
        _contentCache.Clear(); // Очищаем кеш содержимого
        await GetSkillsMetadataAsync(); // Перезагружаем метаданные
    }
}
