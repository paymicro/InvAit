using System.Text.Json;

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
    public List<McpPromptArgument> Arguments { get; set; } = [];
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

    /// <summary>
    /// Full JSON Schema for tool arguments (preserves types, required fields, descriptions)
    /// </summary>
    public JsonElement? InputSchema { get; set; }

    /// <summary>
    /// List of required argument names
    /// </summary>
    public List<string> RequiredArguments { get; set; } = [];

    /// <summary>
    /// Временная переменная, для UI. не стоит доверять.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// MCP Server configuration
/// </summary>
public class McpServerConfig
{
    public string Name { get; set; } = string.Empty;
    public string Transport { get; set; } = "stdio"; // "stdio" or "http"
    public string Command { get; set; } = string.Empty;
    public string[] Args { get; set; } = [];
    public string Url { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public Dictionary<string, string> Env { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public List<McpToolConfig> Tools { get; set; } = [];
}

/// <summary>
/// Root model for mcp.json file deserialization
/// </summary>
public class McpSettingsFile
{
    public Dictionary<string, McpServerJsonEntry> McpServers { get; set; } = [];
}

/// <summary>
/// Single MCP server entry in mcp.json
/// </summary>
public class McpServerJsonEntry
{
    public string? Command { get; set; }

    public string[]? Args { get; set; }

    public string? Url { get; set; }

    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>
/// Ответ тулзы от MCP сервера
/// </summary>
public class MCPToolResult
{
    public List<MCPToolResultContent> Content { get; set; } = [];

    public bool IsError { get; set; }
}

public class MCPToolResultContent
{
    /// <summary>
    /// text, image или resource
    /// </summary>
    public string Type { get; set; } = string.Empty;

    public string? Text { get; set; }

    /// <summary>
    /// Image base64
    /// </summary>
    public string? Data { get; set; }

    public McpResourceContent? Resource { get; set; }

    public string? MimeType { get; set; }
}

public class McpResourceContent
{
    public string Uri { get; set; } = string.Empty;

    public string? MimeType { get; set; }

    public string? Text { get; set; }

    public string? Blob { get; set; }
}
