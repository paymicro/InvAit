namespace Shared.Contracts;

/// <summary>
/// Метаданные скилла для системного промпта (название + описание).
/// Полное содержимое загружается только при активации скилла.
/// </summary>
public class SkillMetadata
{
    /// <summary>Название скилла из YAML frontmatter</summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Краткое описание скилла (триггер для активации)</summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>Относительный путь к SKILL.md файлу</summary>
    public string FilePath { get; set; } = string.Empty;
}

/// <summary>
/// Полное содержимое скилла (загружается только при активации)
/// </summary>
public class SkillContent
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    
    /// <summary>Полное содержимое SKILL.md (без YAML frontmatter)</summary>
    public string Content { get; set; } = string.Empty;
    
    /// <summary>Список ресурсов из секции Resources (для прогрессивной загрузки)</summary>
    public List<string> Resources { get; set; } = new();
}
