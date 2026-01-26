using UIBlazor.Options;

namespace UIBlazor.Services.Settings;

public interface IMcpSettingsProvider : IBaseSettingsProvider
{
    McpOptions Current { get; }
    Task SaveAsync();
    Task<string> RefreshToolsAsync(string serverId);
}
