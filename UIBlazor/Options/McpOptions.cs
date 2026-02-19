using Shared.Contracts.Mcp;

namespace UIBlazor.Options;

public class McpOptions : BaseOptions
{
    /// <summary>
    /// Включен ли вообще MCP
    /// </summary>
    public bool Enabled { get; set => SetIfChanged(ref field, value); } = true;

    /// <summary>
    /// Список MCP серверов
    /// </summary>
    public List<McpServerConfig> Servers { get; set => SetIfChanged(ref field, value); } = [];

    public Dictionary<string, ToolApprovalMode> ServerApprovalModes { get; set => SetIfChanged(ref field, value); } = [];

    /// <summary>
    /// Server initialization errors (serverName -> errorMessage)
    /// </summary>
    public Dictionary<string, string> ServerErrors { get; set => SetIfChanged(ref field, value); } = [];

    /// <summary>
    /// Enabled state for MCP servers (serverName -> enabled)
    /// </summary>
    public Dictionary<string, bool> ServerEnabledStates { get; set => SetIfChanged(ref field, value); } = [];

    /// <summary>
    /// Enabled state for MCP tools (serverName:toolName -> enabled)
    /// </summary>
    public List<string> ToolDisabledStates { get; set => SetIfChanged(ref field, value); } = [];
}
