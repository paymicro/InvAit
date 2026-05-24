using System.Collections.Concurrent;

namespace UIBlazor.Services;

public class ToolCallHandler(IToolManager toolManager) : IToolCallHandler
{
    private readonly ConcurrentDictionary<string, ApprovalWaiter> _approvalWaiters = new();

    public async Task ProcessToolCallsAsync(
        VisualChatMessage message,
        List<ContentSegment> toolSegments,
        CancellationToken cancellationToken)
    {
        if (toolSegments.Count == 0)
            return;

        CancelPendingApprovals();

        // Pre-register approval waiters for all pending segments
        // so users can approve tools in any order
        foreach (var segment in toolSegments)
        {
            if (segment.ApprovalStatus == ToolApprovalStatus.Pending)
            {
                _approvalWaiters[segment.Id] = new ApprovalWaiter(new (), segment);
            }
        }

        foreach (var segment in toolSegments)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var tool = toolManager.GetTool(segment.ToolName);
            var vsToolResult = await ExecuteToolWithApprovalAsync(segment, tool, cancellationToken);

#if DEBUG
            vsToolResult = HeadlessMocker.GetVsToolResult(vsToolResult);
#endif

            message.ToolResults.Add(ToolResult.Convert(vsToolResult, tool?.DisplayName ?? "", tool?.Name ?? ""));
        }
    }

    private async Task<VsToolResult> ExecuteToolWithApprovalAsync(
        ContentSegment segment,
        Tool? tool,
        CancellationToken cancellationToken)
    {
        if (tool == null)
        {
            return VsToolResult.Failed(segment.ToolName, "Tool not found.");
        }

        if (segment.ApprovalStatus == ToolApprovalStatus.Pending)
        {
            segment.ApprovalStatus = await WaitForApprovalAsync(segment, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return VsToolResult.Cancelled(segment.ToolName);
            }
        }

        return await ExecuteToolAsync(tool, segment, cancellationToken);
    }

    private async Task<ToolApprovalStatus> WaitForApprovalAsync(
        ContentSegment segment,
        CancellationToken cancellationToken)
    {
        // Get the pre-registered TCS, or create one if not found (defensive)
        var tcs = _approvalWaiters.GetOrAdd(segment.Id, _ => new ApprovalWaiter(new (), segment));

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
            _approvalWaiters.TryRemove(segment.Id, out _);
        }
    }

    private static async Task<VsToolResult> ExecuteToolAsync(
        Tool tool,
        ContentSegment segment,
        CancellationToken cancellationToken)
    {
        if (segment.ApprovalStatus != ToolApprovalStatus.Approved)
        {
            return VsToolResult.Denied(segment.ToolName);
        }

        var args = segment.ToolName.StartsWith("mcp__")
            ? JsonUtils.DeserializeParameters(string.Join('\n', segment.Lines))
            : segment.ToolParams;

        return await tool.ExecuteAsync(args, cancellationToken);
    }

    public Task HandleApprovalAsync(string segmentId, bool approved)
    {
        var status = approved ? ToolApprovalStatus.Approved : ToolApprovalStatus.Rejected;

        if (_approvalWaiters.TryGetValue(segmentId, out var aw))
        {
            aw.Segment.ApprovalStatus = status;
            aw.TaskSource.TrySetResult(status);
        }

        return Task.CompletedTask;
    }

    private void CancelPendingApprovals()
    {
        foreach (var kvp in _approvalWaiters)
        {
            kvp.Value.Segment.ApprovalStatus = ToolApprovalStatus.Rejected;
            kvp.Value.TaskSource.TrySetCanceled();
        }
        _approvalWaiters.Clear();
    }
}

record ApprovalWaiter(TaskCompletionSource<ToolApprovalStatus> TaskSource, ContentSegment Segment);