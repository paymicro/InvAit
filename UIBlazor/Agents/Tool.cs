namespace UIBlazor.Agents;

public class Tool
{
    /// <summary>
    /// Name of tool
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Display name for UI
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Used for native tools_calling
    /// </summary>
    public required NativeToolDefinition NativeTool { get; init; } = null!;

    /// <summary>
    /// Enabled for use
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Имя MCP сервера
    /// </summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>
    /// Category for grouping tools in UI
    /// </summary>
    public ToolCategory Category { get; init; } = ToolCategory.ReadFiles;

    /// <summary>
    /// Function to execute the tool
    /// </summary>
    [JsonIgnore]
    public required Func<string?, CancellationToken, Task<VsToolResult>> ExecuteAsync { get; init; } = null!;

    /// <summary>
    /// Optional function to execute the tool with tool call context.
    /// Used by delegate_task to attach sub-agent data to the specific tool call.
    /// If null, <see cref="ExecuteAsync"/> is used instead.
    /// </summary>
    [JsonIgnore]
    public Func<string?, ToolCall, CancellationToken, Task<VsToolResult>>? ExecuteWithContextAsync { get; init; }
}
