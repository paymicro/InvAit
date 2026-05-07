namespace ToolCore;

/// <summary>
/// JSON-RPC 2.0 Request
/// </summary>
public class McpRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = string.Empty;
    public object? Params { get; set; }
    public string? Id { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 Response
/// </summary>
public class McpResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public object? Result { get; set; }
    public McpError? Error { get; set; }
    public string? Id { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 Error
/// </summary>
public class McpError
{
    public int Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 Notification (no id)
/// </summary>
public class McpNotification
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = string.Empty;
    public object? Params { get; set; }
}

/// <summary>
/// ToolCore response wrapper
/// </summary>
public class ToolResponse
{
    public bool Success { get; set; } = true;
    public string? Payload { get; set; }
    public string? Error { get; set; }
}
