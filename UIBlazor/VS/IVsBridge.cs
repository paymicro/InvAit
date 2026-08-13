namespace UIBlazor.VS;

public interface IVsBridge
{
    Task InitializeAsync();

    /// <summary>
    /// Выполнение тулзы
    /// </summary>
    /// <param name="name">Имя тулзы</param>
    /// <param name="args">Сериализованный объект</param>
    Task<VsToolResult> ExecuteToolAsync(string name, string? args, CancellationToken cancellationToken = default);
}
