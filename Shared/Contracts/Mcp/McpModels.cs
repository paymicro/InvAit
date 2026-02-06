namespace Shared.Contracts.Mcp;

/// <summary>
/// MCP Tool definition
/// </summary>
public class McpTool
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public object InputSchema { get; set; } = new();
}

/// <summary>
/// MCP Resource definition
/// </summary>
public class McpResource
{
    public string Uri { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? MimeType { get; set; }
}

/// <summary>
/// MCP Prompt definition
/// </summary>
public class McpPrompt
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<McpPromptArgument> Arguments { get; set; } = new();
}

/// <summary>
/// MCP Prompt argument
/// </summary>
public class McpPromptArgument
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Required { get; set; }
}

/// <summary>
/// MCP Tool configuration for settings
/// </summary>
public class McpToolConfig
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// MCP Server configuration
/// </summary>
public class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Transport { get; set; } = "stdio"; // "stdio" or "http"
    public string Command { get; set; } = "npx";
    public string Args { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<McpToolConfig> Tools { get; set; } = [];
}
