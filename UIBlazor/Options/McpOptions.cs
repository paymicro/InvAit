using Shared.Contracts.Mcp;

namespace UIBlazor.Options;

public class McpOptions : BaseOptions
{
    public bool Enabled { get => field; set => SetIfChanged(ref field, value); } = true;

    public List<McpServerConfig> Servers { get => field; set => SetIfChanged(ref field, value); } = new();
}
