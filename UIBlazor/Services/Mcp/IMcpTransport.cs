using Shared.Contracts.Mcp;

namespace UIBlazor.Services.Mcp;

/// <summary>
/// Интерфейс транспортного слоя MCP
/// </summary>
public interface IMcpTransport : IDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task SendAsync(McpRequest request, CancellationToken cancellationToken = default);
    Task<McpResponse> ReceiveAsync(CancellationToken cancellationToken = default);
    bool IsConnected { get; }
}
