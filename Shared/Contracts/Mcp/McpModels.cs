using System.Text.Json;
using System.Text.Json.Serialization;

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
    public Dictionary<string, string> Headers { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public List<McpToolConfig> Tools { get; set; } = [];
}

/// <summary>
/// Root model for mcp.json. Accepts both OpenCode-style root "mcp" and Claude/Cursor-style "mcpServers".
/// Both roots load together in one file; "mcp" wins on name conflicts.
/// </summary>
public class McpSettingsFile
{
    public Dictionary<string, McpServerJsonEntry>? Mcp { get; set; }

    public Dictionary<string, McpServerJsonEntry>? McpServers { get; set; }

    /// <summary>Merged servers from both roots; on name conflicts the "mcp" entry wins. Ordering: "mcp" entries first, then "mcpServers".</summary>
    public Dictionary<string, McpServerJsonEntry> GetServers()
    {
        var merged = new Dictionary<string, McpServerJsonEntry>(StringComparer.Ordinal);
        foreach (var pair in Mcp ?? []) merged[pair.Key] = pair.Value;
        foreach (var pair in McpServers ?? []) if (!merged.ContainsKey(pair.Key)) merged.Add(pair.Key, pair.Value);
        return merged;
    }
}

/// <summary>
/// Single MCP server entry in mcp.json.
/// type: "local" (default) requires command; "remote" requires url.
/// </summary>
public class McpServerJsonEntry
{
    /// <summary>"local" | "remote"; absent means local.</summary>
    public string? Type { get; set; }

    /// <summary>Executable plus optional arguments. Accepts a single string or an array.</summary>
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public string[]? Command { get; set; }

    public string[]? Args { get; set; }

    public string? Url { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public Dictionary<string, string>? Env { get; set; }

    public Dictionary<string, string>? Environment { get; set; }

    public bool? Enabled { get; set; }

    public bool? Oauth { get; set; }

    [JsonIgnore]
    public string? CommandProgram => Command is { Length: > 0 } ? Command[0] : null;

    [JsonIgnore]
    public IEnumerable<string> EffectiveArgs
        => (Command?.Skip(1) ?? []).Concat(Args ?? []);

    [JsonIgnore]
    public Dictionary<string, string>? EffectiveEnv => Env ?? Environment;
}

/// <summary>Reads either "cmd" or ["cmd", "-y", ...]; writes back a bare string for single-element arrays.</summary>
public class StringOrStringArrayConverter : JsonConverter<string[]?>
{
    public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => [reader.GetString()!],
            JsonTokenType.StartArray => ReadArray(ref reader),
            _ => throw new JsonException("Expected a string or an array of strings."),
        };
    }

    private static string[] ReadArray(ref Utf8JsonReader reader)
    {
        var values = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return [.. values];

            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Expected a string element in array.");

            values.Add(reader.GetString()!);
        }

        throw new JsonException("Unterminated array.");
    }

    public override void Write(Utf8JsonWriter writer, string[]? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
        }
        else if (value.Length == 1)
        {
            writer.WriteStringValue(value[0]);
        }
        else
        {
            writer.WriteStartArray();
            foreach (var item in value)
                writer.WriteStringValue(item);
            writer.WriteEndArray();
        }
    }
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
