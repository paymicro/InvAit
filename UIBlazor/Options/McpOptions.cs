using Shared.Contracts.Mcp;

namespace UIBlazor.Options;

public class McpOptions : BaseOptions
{
    public bool Enabled { get; set => SetIfChanged(ref field, value); } = true;

    public List<McpServerConfig> Servers { get; set => SetIfChanged(ref field, value); } = [];
}
