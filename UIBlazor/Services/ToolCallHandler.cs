using System.Collections.Concurrent;

namespace UIBlazor.Services;

public class ToolCallHandler(IToolManager toolManager) : IToolCallHandler
{
    private readonly ConcurrentDictionary<string, ApprovalWaiter> _approvalWaiters = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _askUserWaiters = new();

    /// <inheritdoc />
    public event Action<string>? ApprovalRequired;

    public void PrepareToolsForApprovals(List<ToolCall> toolCalls)
    {
        CancelPendingApprovals();

        string? firstPendingId = null;

        // Pre-register approval waiters for all pending segments
        // so users can approve tools in any order
        foreach (var toolCall in toolCalls)
        {
            toolCall.IsReady = true;
            toolCall.Function.Arguments = CleanJsonArguments(toolCall.Function.Arguments);

            // AskUser always requires user interaction (wait for answer)
            if (toolCall.Function.Name == BasicEnum.AskUser)
            {
                toolCall.ApprovalStatus = ToolApprovalStatus.Pending;
                _askUserWaiters[toolCall.Id] = new TaskCompletionSource<string>();
                firstPendingId ??= toolCall.Id;
                continue;
            }

            var approvalMode = toolManager.GetApprovalModeByToolName(toolCall.Function.Name);
            switch (approvalMode)
            {
                case ToolApprovalMode.Ask:
                    toolCall.ApprovalStatus = ToolApprovalStatus.Pending;
                    _approvalWaiters[toolCall.Id] = new ApprovalWaiter(new(), toolCall);
                    firstPendingId ??= toolCall.Id;
                    break;
                case ToolApprovalMode.Deny:
                    toolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
                    break;
                case ToolApprovalMode.Allow:
                default:
                    toolCall.ApprovalStatus = ToolApprovalStatus.Approved;
                    break;
            }
        }

        // Notify subscribers that user action is required
        if (firstPendingId is not null)
        {
            ApprovalRequired?.Invoke(firstPendingId);
        }
    }

    public async Task ProcessToolCallsAsync(
        List<ToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
        // Separate delegate_task calls (can run in parallel) from other tool calls (sequential).
        // Each sub-agent has its own ToolCallHandler, so approvals are isolated.
        // Non-delegate tools must remain sequential because they share the same approval waiters.
        var delegateTasks = new List<(ToolCall toolCall, Tool? tool)>();
        var sequentialTasks = new List<(ToolCall toolCall, Tool? tool)>();

        foreach (var toolCall in toolCalls)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var tool = toolManager.GetTool(toolCall.Function.Name);

            if (toolCall.Function.Name == BuiltInToolEnum.DelegateTask)
                delegateTasks.Add((toolCall, tool));
            else
                sequentialTasks.Add((toolCall, tool));
        }

        // Run delegate_task calls in parallel
        if (delegateTasks.Count > 0)
        {
            var parallelTasks = delegateTasks.Select(async item =>
            {
                var vsToolResult = await ExecuteToolWithApprovalAsync(item.toolCall, item.tool, cancellationToken);
#if DEBUG
                vsToolResult = HeadlessMocker.GetVsToolResult(vsToolResult);
#endif
                item.toolCall.Result = ToolResult.Convert(vsToolResult, item.tool?.DisplayName ?? "", item.tool?.Name ?? "");
            });

            await Task.WhenAll(parallelTasks);
        }

        // Run other tool calls sequentially
        foreach (var (toolCall, tool) in sequentialTasks)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var vsToolResult = await ExecuteToolWithApprovalAsync(toolCall, tool, cancellationToken);

#if DEBUG
            vsToolResult = HeadlessMocker.GetVsToolResult(vsToolResult);
#endif

            toolCall.Result = ToolResult.Convert(vsToolResult, tool?.DisplayName ?? "", tool?.Name ?? "");
        }
    }

    private async Task<VsToolResult> ExecuteToolWithApprovalAsync(
        ToolCall toolCall,
        Tool? tool,
        CancellationToken cancellationToken)
    {
        if (tool == null)
        {
            return VsToolResult.Failed(toolCall.Function.Name, "Tool not found.");
        }

        // AskUser: wait for user to select an option or type a custom answer
        if (toolCall.Function.Name == BasicEnum.AskUser && _askUserWaiters.TryGetValue(toolCall.Id, out var askTcs))
        {
            try
            {
                var answer = await askTcs.Task.WaitAsync(cancellationToken);
                _askUserWaiters.TryRemove(toolCall.Id, out _);
                toolCall.ApprovalStatus = ToolApprovalStatus.Approved;
                return new VsToolResult
                {
                    Name = BasicEnum.AskUser,
                    Success = true,
                    Result = answer
                };
            }
            catch (OperationCanceledException)
            {
                _askUserWaiters.TryRemove(toolCall.Id, out _);
                return VsToolResult.Cancelled(toolCall.Function.Name);
            }
        }

        if (toolCall.ApprovalStatus == ToolApprovalStatus.Pending)
        {
            toolCall.ApprovalStatus = await WaitForApprovalAsync(toolCall, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return VsToolResult.Cancelled(toolCall.Function.Name);
            }
        }

        return await ExecuteToolAsync(tool, toolCall, cancellationToken);
    }

    private async Task<ToolApprovalStatus> WaitForApprovalAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken)
    {
        // Get the pre-registered TCS, or create one if not found (defensive)
        var tcs = _approvalWaiters.GetOrAdd(toolCall.Id, _ => new ApprovalWaiter(new(), toolCall));

        try
        {
            return await tcs.TaskSource.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CancelPendingApprovals();
            return ToolApprovalStatus.Rejected;
        }
        finally
        {
            _approvalWaiters.TryRemove(toolCall.Id, out _);
        }
    }

    private static string CleanJsonArguments(string rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
            return "{}";

        var trimmed = rawArguments.Trim();

        // Находим первый символ '{' и ПОСЛЕДНИЙ символ '}'
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');

        // Если скобки найдены и они расположены правильно
        if (firstBrace != -1 && lastBrace > firstBrace)
        {
            // Вырезаем только то, что находится внутри объекта включительно
            return trimmed.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return "{}"; // Фолбэк, если структура вообще нарушена
    }

    private static async Task<VsToolResult> ExecuteToolAsync(
        Tool tool,
        ToolCall toolCall,
        CancellationToken cancellationToken)
    {
        if (toolCall.ApprovalStatus != ToolApprovalStatus.Approved)
        {
            return VsToolResult.Denied(toolCall.Function.Name);
        }

        // Use context-aware execution if available (for delegate_task)
        if (tool.ExecuteWithContextAsync is not null)
        {
            return await tool.ExecuteWithContextAsync(toolCall.Function.Arguments, toolCall, cancellationToken);
        }

        return await tool.ExecuteAsync(toolCall.Function.Arguments, cancellationToken);
    }

    public Task HandleApprovalAsync(string toolCallId, bool approved)
    {
        var status = approved ? ToolApprovalStatus.Approved : ToolApprovalStatus.Rejected;

        if (_approvalWaiters.TryGetValue(toolCallId, out var aw))
        {
            aw.ToolCall.ApprovalStatus = status;
            aw.TaskSource.TrySetResult(status);
        }

        return Task.CompletedTask;
    }

    public Task HandleAskUserAnswerAsync(string toolCallId, string answer)
    {
        if (_askUserWaiters.TryGetValue(toolCallId, out var tcs))
        {
            tcs.TrySetResult(answer);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels all pending approval and ask_user waiters.
    /// Called internally by PrepareToolsForApprovals before setting up new waiters,
    /// and externally by SubAgentExecutor in a finally block for cleanup.
    /// </summary>
    public void CancelPendingApprovals()
    {
        foreach (var kvp in _approvalWaiters)
        {
            kvp.Value.ToolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
            kvp.Value.TaskSource.TrySetCanceled();
        }
        _approvalWaiters.Clear();

        foreach (var kvp in _askUserWaiters)
        {
            kvp.Value.TrySetCanceled();
        }
        _askUserWaiters.Clear();
    }
}

internal record ApprovalWaiter(TaskCompletionSource<ToolApprovalStatus> TaskSource, ToolCall ToolCall);
