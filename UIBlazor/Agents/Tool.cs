namespace UIBlazor.Agents;

public class Tool
{
    public Tool()
    {
    }

    public Tool(Func<Tool, IReadOnlyDictionary<string, object>, Task<VsToolResult>> executeWithToolAsync)
    {
        ExecuteAsync = args => executeWithToolAsync(this, args);
    }

    /// <summary>
    /// Name of tool
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Description for LLM
    /// </summary>
    public string Description { get; init; }

    /// <summary>
    /// Example in system message
    /// </summary>
    public string ExampleToSystemMessage { get; init; }

    /// <summary>
    /// Description for user for Approval request
    /// TODO
    /// </summary>
    public string ApprovalDescription { get; init; }

    /// <summary>
    /// Enabled for use
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Approval behavior
    /// </summary>
    public bool ApprovalNeeded { get; init; } = true;

    /// <summary>
    /// Category for grouping tools in UI
    /// </summary>
    public string Category { get; init; } = "General";

    /// <summary>
    /// Function to execute the tool
    /// </summary>
    public Func<IReadOnlyDictionary<string, object>, Task<VsToolResult>> ExecuteAsync { get; init; } = null!;

    /// <summary>
    /// Logging ResponseAndRequest
    /// </summary>
    public bool LogResponseAndRequest { get; set; }
}
