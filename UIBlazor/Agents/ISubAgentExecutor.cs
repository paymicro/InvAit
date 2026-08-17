namespace UIBlazor.Agents;

/// <summary>
/// Executes sub-agent tasks delegated by the main agent via the delegate_task tool.
/// </summary>
public interface ISubAgentExecutor
{
    /// <summary>
    /// Raised when any sub-agent's state changes (status, messages, content streaming, tool calls).
    /// AiChat subscribes to this to trigger Blazor re-rendering during sub-agent execution.
    /// </summary>
    event Action<SubAgentMessage>? SubAgentStateChanged;

    /// <summary>
    /// Executes a sub-agent task with the given parameters.
    /// </summary>
    /// <param name="argsJson">JSON arguments containing task, systemPrompt, allowedTools, deniedTools.</param>
    /// <param name="toolCall">The tool call to attach the sub-agent data to (for UI display).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sub-agent's final result.</returns>
    Task<VsToolResult> ExecuteAsync(string argsJson, ToolCall toolCall, CancellationToken cancellationToken);
}
