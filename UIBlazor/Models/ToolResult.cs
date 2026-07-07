namespace UIBlazor.Models;

/// <summary>
/// Класс для хранения ответов тулзов в <see cref="VisualChatMessage"/>
/// </summary>
public class ToolResult
{
    /// <summary>
    /// Имя тулзы
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Tool call ID from native API (used for role: "tool" responses).
    /// </summary>
    public string ToolCallId { get; init; } = string.Empty;

    /// <summary>
    /// Локализованное имя тулзы. Только для UI
    /// </summary>
    [JsonIgnore]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Полное содержание ответа включая теги <tool_result></tool_result>
    /// </summary>
    public string Content { get; init; } = string.Empty;

    [JsonIgnore]
    public string GetDisplayContent => string.Join("\n", Content.Split('\n').Skip(1).SkipLast(1)).Trim();

    /// <summary>
    /// Статус
    /// </summary>
    public bool Success { get; set; }

    public static ToolResult Convert(VsToolResult vsToolResult, string displayName, string name, string toolCallId = "")
        => new()
        {
            Name = name,
            DisplayName = GetDisplayName(vsToolResult.Success, !string.IsNullOrEmpty(displayName) ? displayName : name),
            ToolCallId = toolCallId,
            Content = vsToolResult.Success ? vsToolResult.Result : vsToolResult.ErrorMessage,
            Success = vsToolResult.Success
        };

    public static string GetDisplayName(bool success, string displayName)
        => $"{(success ? '✅' : '❌')} {displayName}";
}
