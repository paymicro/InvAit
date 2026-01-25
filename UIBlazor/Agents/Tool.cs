using Shared.Contracts;

namespace UIBlazor.Agents;

public class Tool
{
    public Tool()
    {
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
    /// </summary>
    public string ApprovalDescription { get; init; }

    /// <summary>
    /// Enabled for use
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Approval behavior
    /// </summary>
    public ToolApprovalMode ApprovalMode { get; set; } = ToolApprovalMode.Always;

    /// <summary>
    /// Category for grouping tools in UI
    /// </summary>
    public ToolCategory Category { get; init; } = ToolCategory.FileSystem;

    /// <summary>
    /// Function to execute the tool
    /// </summary>
    public Func<IReadOnlyDictionary<string, object>, Task<VsToolResult>> ExecuteAsync { get; init; } = null!;
}
