namespace UIBlazor.Models;

/// <summary>
/// Класс для хранения ответов тулзов в <see cref="VisualChatMessage"/>
/// </summary>
public class ToolResult
{
    /// <summary>
    /// Имя тулзы
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Локализованное имя тулзы. Только для UI
    /// </summary>
    [JsonIgnore]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Полное содержание ответа включая теги <tool_result></tool_result>
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonIgnore]
    public string GetDisplayContent => string.Join("\n", Content.Split('\n').Skip(1).SkipLast(1)).Trim();

    /// <summary>
    /// Статус
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Максимальный размер содержимого результата инструмента в символах.
    /// ~300 KB — достаточно для чтения нескольких файлов с бэкендным лимитом 2000 строк,
    /// и безопасно для localStorage (лимит WebView2 ~5-10 MB на домен).
    /// </summary>
    private const int MaxContentLength = 300_000;

    public static ToolResult Convert(VsToolResult vsToolResult, string displayName, string name)
    {
        var rawContent = vsToolResult.Success ? vsToolResult.Result : vsToolResult.ErrorMessage;
        return new()
        {
            Name = name,
            DisplayName = GetDisplayName(vsToolResult.Success, !string.IsNullOrEmpty(displayName) ? displayName : name),
            Content = TruncateContent(rawContent),
            Success = vsToolResult.Success
        };
    }

    private static string TruncateContent(string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= MaxContentLength)
            return content;

        var truncated = content.Length - MaxContentLength;
        return $"[... {truncated:N0} characters truncated from the beginning ...]\n{content[^MaxContentLength..]}";
    }

    public static string GetDisplayName(bool success, string displayName)
        => $"{(success ? '✅' : '❌')} {displayName}";
}
