using System.ComponentModel;

namespace UIBlazor.Models;

/// <summary>
/// Одна делегированная задача в вызове delegate_task.
/// Один вызов delegate_task несёт список таких задач; оркестратор запускает их
/// в параллельные ветки (Task.WhenAll) и ждёт все.
/// </summary>
public sealed class SubAgentTaskDefinition
{
    [Description("Clear task description for the sub-agent")]
    public string Task { get; set; } = string.Empty;

    [Description("System prompt defining the sub-agent's role and expertise")]
    public string SystemPrompt { get; set; } = string.Empty;

    [Description("List of tool names the sub-agent is allowed to use. If null or empty, all tools are available. Only these tools will be accessible; all others are denied.")]
    public string[]? AllowedTools { get; set; }
}
