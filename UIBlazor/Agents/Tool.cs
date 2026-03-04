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
    /// Description for LLM
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Example in system message
    /// </summary>
    public string ExampleToSystemMessage { get; init; } = string.Empty;

    /// <summary>
    /// Enabled for use
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Category for grouping tools in UI
    /// </summary>
    public ToolCategory Category { get; init; } = ToolCategory.ReadFiles;

    /// <summary>
    /// Function to execute the tool
    /// </summary>
    [JsonIgnore]
    public Func<IReadOnlyDictionary<string, object>, Task<VsToolResult>> ExecuteAsync { get; init; } = null!;
}
