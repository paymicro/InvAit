using Shared.Contracts;
using UIBlazor.Agents;

namespace UIBlazor.VS;

public interface IVsBridge
{
    event Action<AppMode>? OnModeSwitched;

    Task<VsToolResult> ExecuteToolAsync(string name, IReadOnlyDictionary<string, object>? args = null);

    Task SwitchModeAsync(AppMode mode);
}
