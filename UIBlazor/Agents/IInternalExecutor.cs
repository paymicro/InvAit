namespace UIBlazor.Agents;

public interface IInternalExecutor
{
    Task<VsToolResult> ExecuteToolAsync(string name, string args, CancellationToken cancellationToken);

    /// <summary>
    /// Executes an internal tool with the tool call context.
    /// Needed for delegate_task to attach sub-agent data to the specific tool call.
    /// </summary>
    Task<VsToolResult> ExecuteToolAsync(string name, string args, ToolCall? toolCall, CancellationToken cancellationToken);
}
