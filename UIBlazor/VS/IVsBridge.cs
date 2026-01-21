using UIBlazor.Agents;

namespace UIBlazor.VS;

public interface IVsBridge
{
    Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object>? args = null);
}
