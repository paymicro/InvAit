namespace UIBlazor.VS;

/// <summary>
/// Shared helpers over <see cref="IVsBridge"/> (used by file tool views).
/// </summary>
public static class VsBridgeExtensions
{
    /// <summary>
    /// Открывает файл в IDE (open_file уже реализован на хостах VS/VS Code).
    /// Возвращает результат; исключения моста проглатываются (лог в обычный консольный канал,
    /// не в Console.Error) — падение моста не должно крешить UI и не должно поднимать
    /// плашку #blazor-error-ui из-за недоступного файла.
    /// </summary>
    public static async Task<VsToolResult> OpenFileInIdeAsync(this IVsBridge bridge, string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return VsToolResult.Failed(BasicEnum.OpenFile, "File path is empty");
        }

        try
        {
            return await bridge.ExecuteToolAsync(BasicEnum.OpenFile, filePath);
        }
        catch (Exception ex)
        {
            await ConsoleUtils.WriteLineAsync($"Failed to open file '{filePath}': {ex.Message}", CancellationToken.None);
            return VsToolResult.Failed(BasicEnum.OpenFile, ex.Message);
        }
    }
}
