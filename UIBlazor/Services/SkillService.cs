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
    public async Task<List<SkillMetadata>> GetSkillsMetadataAsync(CancellationToken cancellationToken)
    {
        // Проверяем кеш (обновляем раз в 2 минуты или по запросу)
        if (_skillsCache != null && (DateTime.UtcNow - _lastCacheUpdate).TotalMinutes < 2)
        {
            return _skillsCache;
        }

        var result = await vsBridge.ExecuteToolAsync(BasicEnum.GetSkillsMetadata, null, cancellationToken);
#if DEBUG
        result = HeadlessMocker.GetVsToolResult(result);
#endif
        if (!result.Success)
        {
            return _skillsCache ?? [];
        }

        try
        {
            var metadataJson = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(result.Result);
            _skillsCache = metadataJson?.Select(m => new SkillMetadata
            {
                Name = m.GetValueOrDefault("name", ""),
                Description = m.GetValueOrDefault("description", "")
            }).ToList() ?? [];

            _lastCacheUpdate = DateTime.UtcNow;
            return _skillsCache;
        }
        catch
        {
            return _skillsCache ?? [];
        }
    }

    /// <summary>
    /// Загрузить полное содержимое скилла (с кешированием)
    /// Вызывается только когда агент активирует скилл
    /// </summary>
    public async Task<SkillContent?> LoadSkillContentAsync(string skillName, CancellationToken cancellationToken)
    {
        // Проверяем кеш содержимого
        if (_contentCache.TryGetValue(skillName, out var cachedContent))
        {
            return cachedContent;
        }

        var args = JsonUtils.SerializeCompact(new { skillName });
        var result = await vsBridge.ExecuteToolAsync(BasicEnum.ReadSkillContent, args, cancellationToken);
#if DEBUG
        result = HeadlessMocker.GetVsToolResult(result);
#endif
        if (!result.Success)
        {
            return null;
        }

        try
        {
            var skillContent = JsonUtils.Deserialize<SkillContent>(result.Result);
            if (skillContent == null)
                return null;

            // Кешируем содержимое (максимум 10 скиллов в кеше)
            if (_contentCache.Count >= 10)
            {
                var oldestKey = _contentCache.Keys.First();
                _contentCache.Remove(oldestKey);
            }
            _contentCache[skillName] = skillContent;

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
        if (skills.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Available Skills");
        sb.AppendLine();
        sb.AppendLine($"You have access to the following skills. Skills are specialized instructions that you can activate by requesting them when relevant (tool `{BasicEnum.ReadSkillContent}`).");
        sb.AppendLine($"Some skills may include reference materials or scripts — load them on demand by passing the optional `fileName` parameter (relative path within the skill folder, e.g. 'references/api-spec.md').");

        foreach (var skill in skills)
        {
            sb.AppendLine($"- **{skill.Name}**: {skill.Description}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Принудительно обновить кеш скиллов
    /// При изменении файлов (через FileSystemWatcher)
    /// </summary>
    public async Task RefreshCacheAsync(CancellationToken cancellationToken)
    {
        _lastCacheUpdate = DateTime.MinValue; // Сбрасываем кеш
        _contentCache.Clear(); // Очищаем кеш содержимого
        await GetSkillsMetadataAsync(cancellationToken); // Перезагружаем метаданные
    }

    public async Task<VsToolResult> LoadSkillContentMarkDownAsync(string args, CancellationToken cancellationToken)
    {
        var argsDict = JsonUtils.DeserializeParameters(args);
        var skillName = argsDict?.GetString("skillName");
        var fileName = argsDict?.GetString("fileName");

        if (string.IsNullOrEmpty(skillName))
        {
            return new VsToolResult
            {
                Success = false,
                ErrorMessage = "Skill name is missing"
            };
        }

        // If fileName is provided, read a specific file from the skill folder via vsBridge
        if (!string.IsNullOrEmpty(fileName))
        {
            var requestArgs = JsonUtils.SerializeCompact(new { skillName, fileName });
            var result = await vsBridge.ExecuteToolAsync(BasicEnum.ReadSkillContent, requestArgs, cancellationToken);
#if DEBUG
            result = HeadlessMocker.GetVsToolResult(result);
#endif
            if (!result.Success)
            {
                return result;
            }

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"## File: {fileName} (skill: {skillName})");
            sb.AppendLine(result.Result);

            return new VsToolResult { Result = sb.ToString() };
        }

        // No fileName — load SKILL.md content (with caching)
        var skillContent = await LoadSkillContentAsync(skillName, cancellationToken);

        if (skillContent == null)
        {
            return new VsToolResult
            {
                Success = false,
                ErrorMessage = "<empty>"
            };
        }
        var sb2 = new StringBuilder();
        sb2.AppendLine();
        sb2.AppendLine($"## Skill {skillName}");
        sb2.AppendLine(skillContent.Content);

        if (skillContent.Files.Count > 0)
        {
            sb2.AppendLine();
            sb2.AppendLine("### Available files in skill folder:");
            foreach (var file in skillContent.Files)
            {
                sb2.AppendLine($"- `{file}`");
            }
        }

        return new VsToolResult { Result = sb2.ToString() };
    }
}
