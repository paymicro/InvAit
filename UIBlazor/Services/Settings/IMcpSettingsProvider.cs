using Shared.Contracts.Mcp;

namespace UIBlazor.Services.Settings;

public interface IMcpSettingsProvider : IBaseSettingsProvider
{
    McpOptions Current { get; }

    Task LoadAsync();

    Task SaveAsync();

    Task OpenSettingsFileAsync();

    Task<string> RefreshToolsAsync(McpServerConfig server);
    Task StopAllAsync();
}
