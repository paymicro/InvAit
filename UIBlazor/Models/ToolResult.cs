namespace UIBlazor.Models;

/// <summary>
/// Класс для хранения ответов тулзов в <see cref="VisualChatMessage"/>
/// </summary>
public class ToolResult
{
    [JsonIgnore]
    public string Id { get; set; } = $"tool{Guid.NewGuid()}";

    /// <summary>
    /// Имя тулзы
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Локализованное имя тулзы. Только для UI
    /// </summary>
    [JsonIgnore]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Полное содержание ответа включая теги <tool_result></tool_result>
    /// </summary>
    public string Content { get; set; } = string.Empty;

    public string GetDisplayContent => string.Join("\n", Content.Split('\n').Skip(1).SkipLast(1));

    /// <summary>
    /// Статус
    /// </summary>
    public bool Success { get; set; }

    public static ToolResult Convert(VsToolResult vsToolResult, string displayName, string name)
    {
        return new ToolResult
        {
            DisplayName = GetDisplayName(vsToolResult.Success, !string.IsNullOrEmpty(displayName) ? displayName : name),
            Name = name,
            Content = $"""
                       <tool_result name="{name}" success={vsToolResult.Success}>
                       {(vsToolResult.Success ? vsToolResult.Result : vsToolResult.ErrorMessage)}
                       </tool_result>
                       """,
            Success = vsToolResult.Success
        };
    }

    public static string GetDisplayName(bool success, string displayName)
        => $"{(success ? '✅' : '❌')} {displayName}";
}
