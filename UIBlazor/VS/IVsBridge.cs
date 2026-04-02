namespace UIBlazor.VS;

public interface IVsBridge
{
    Task InitializeAsync();

    Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object>? args = null, CancellationToken cancellationToken = default);
}
