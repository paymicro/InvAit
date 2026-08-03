using System.Collections.Concurrent;

namespace UIBlazor.Services;

public class ToolCallHandler(IToolManager toolManager) : IToolCallHandler
{
    private readonly ConcurrentDictionary<string, ApprovalWaiter> _approvalWaiters = new();

    public void PrepareToolsForApprovals(List<ToolCall> toolCalls)
    {
        CancelPendingApprovals();

        // Pre-register approval waiters for all pending segments
        // so users can approve tools in any order
        foreach (var toolCall in toolCalls)
        {
            toolCall.IsReady = true;
            var approvalMode = toolManager.GetApprovalModeByToolName(toolCall.Function.Name);
            switch (approvalMode)
            {
                case ToolApprovalMode.Ask:
                    toolCall.ApprovalStatus = ToolApprovalStatus.Pending;
                    _approvalWaiters[toolCall.Id] = new ApprovalWaiter(new(), toolCall);
                    break;
                case ToolApprovalMode.Deny:
                    toolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
                    break;
                default:
                    toolCall.ApprovalStatus = ToolApprovalStatus.Approved;
                    break;
            }
        }
    }

    public async Task ProcessToolCallsAsync(
        List<ToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
        foreach (var toolCall in toolCalls)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var tool = toolManager.GetTool(toolCall.Function.Name);
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
        var tcs = _approvalWaiters.GetOrAdd(toolCall.Id, _ => new ApprovalWaiter(new (), toolCall));

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

    private static async Task<VsToolResult> ExecuteToolAsync(
        Tool tool,
        ToolCall toolCall,
        CancellationToken cancellationToken)
    {
        if (toolCall.ApprovalStatus != ToolApprovalStatus.Approved)
        {
            return VsToolResult.Denied(toolCall.Function.Name);
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

    private void CancelPendingApprovals()
    {
        foreach (var kvp in _approvalWaiters)
        {
            kvp.Value.ToolCall.ApprovalStatus = ToolApprovalStatus.Rejected;
            kvp.Value.TaskSource.TrySetCanceled();
        }
        _approvalWaiters.Clear();
    }
}

record ApprovalWaiter(TaskCompletionSource<ToolApprovalStatus> TaskSource, ToolCall ToolCall);