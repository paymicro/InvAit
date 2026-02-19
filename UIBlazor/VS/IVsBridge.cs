namespace UIBlazor.VS;

public interface IVsBridge
{
    event Action<AppMode>? OnModeSwitched;

    Task InitializeAsync();

    Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object>? args = null);
}
