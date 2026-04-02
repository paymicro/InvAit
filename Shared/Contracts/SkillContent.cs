namespace Shared.Contracts;

/// <summary>
/// Полное содержимое скилла (загружается только при активации)
/// </summary>
public class SkillContent
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Полное содержимое SKILL.md (без YAML frontmatter)
    /// </summary>
    public string Content { get; set; } = string.Empty;
}
