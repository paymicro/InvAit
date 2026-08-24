namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    /// <summary>
    /// Sets up a sub-agent run whose first LLM response contains one tool call for a tool
    /// that requires user approval (ToolApprovalMode.Ask) and blocks inside ExecuteAsync
    /// until <paramref name="releaseTool"/> completes.
    /// Returns an accessor for the LLM-produced ToolCall (valid after the first completions call).
    /// </summary>
    private Func<ToolCall?> SetupApprovalScenario(
        string toolName,
        TaskCompletionSource releaseTool,
        TaskCompletionSource toolExecuted)
    {
        var execTool = new Tool
        {
            Name = toolName,
            DisplayName = toolName,
            Category = ToolCategory.Execution,
            NativeTool = new NativeToolDefinition
            {
                Function = new NativeToolFunction
                {
                    Name = toolName,
                    Description = $"Test tool {toolName}",
                    Parameters = new NativeParameters { Type = NativeToolType.Object, Properties = [], Required = [] }
                }
            },
            ExecuteAsync = async (_, _) =>
            {
                toolExecuted.TrySetResult();
                await releaseTool.Task;
                return new VsToolResult { Success = true, Result = "tool done" };
            }
        };

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool> { execTool });
        _toolManagerMock.Setup(x => x.GetTool(toolName)).Returns(execTool);
        _toolManagerMock.Setup(x => x.GetApprovalModeByToolName(toolName)).Returns(ToolApprovalMode.Ask);

        var completionsCallCount = 0;
        ToolCall? captured = null;

        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(), It.IsAny<string>(), It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (_, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    if (completionsCallCount == 1)
                    {
                        captured = new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = toolName, Arguments = "{}" } };
                        resultCapture.AccumulatedToolCalls = [captured];
                    }
                    else
                    {
                        resultCapture.AccumulatedToolCalls = null;
                    }
                })
            .Returns(CreateEmptyDeltaStream());

        var processStreamCallCount = 0;
        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(), It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(), It.IsAny<Action<List<ToolCall>>>(), It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(), It.IsAny<CancellationToken>()))
            .Callback<VisualChatMessage, IAsyncEnumerable<ChatDelta>, Action<string>?, Action<List<ToolCall>>, Action?, CompletionsResult, CancellationToken>(
                (msg, _, onContent, _, _, _, _) =>
                {
                    processStreamCallCount++;
                    if (processStreamCallCount == 2)
                    {
                        msg.Content = "Final answer";
                        onContent?.Invoke("Final answer");
                    }
                })
            .Returns(Task.CompletedTask);

        return () => captured;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for condition.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Approval_Required_PausesLoop_AndApproveExecutesViaSubAgentHandler()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });

        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmToolCallAccessor = SetupApprovalScenario("slow_tool", releaseTool, toolExecuted);

        var structural = new List<SubAgentMessage>();
        _executor.SubAgentStateChanged += sa => structural.Add(sa);

        // Act — run until the approval is requested
        var runTask = _executor.ExecuteAsync(args, toolCall, CancellationToken.None);
        var subAgent = toolCall.SubAgent!;

        await WaitUntilAsync(() => subAgent.PendingToolCallId == "tc1");

        // Paused: pending status, no execution yet, expanded so the user sees the request
        var llmToolCall = llmToolCallAccessor();
        Assert.NotNull(llmToolCall);
        Assert.Equal(ToolApprovalStatus.Pending, llmToolCall!.ApprovalStatus);
        Assert.False(toolExecuted.Task.IsCompleted, "Tool must not execute before approval");
        Assert.True(subAgent.IsExpanded);
        Assert.Contains(structural, sa => ReferenceEquals(sa, subAgent) && sa.PendingToolCallId == "tc1");

        // Approve via the SUB-AGENT's handler (isolated routing)
        await subAgent.ToolCallHandler!.HandleApprovalAsync("tc1", approved: true);

        // The tool now runs; unblock it and finish the loop
        await toolExecuted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseTool.TrySetResult();

        var result = await runTask;

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Final answer", result.Result);
        Assert.Equal(SubAgentStatus.Completed, subAgent.Status);
        Assert.Equal(ToolApprovalStatus.Approved, llmToolCall!.ApprovalStatus);
        // user task + assistant(toolcall) + assistant(final)
        Assert.Equal(3, subAgent.GetMessageCount());
    }

    [Fact]
    public async Task Approval_Rejected_ToolNotExecuted_LoopContinues()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });

        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmToolCallAccessor = SetupApprovalScenario("slow_tool", releaseTool, toolExecuted);

        // Act — wait for the approval request, then REJECT it
        var runTask = _executor.ExecuteAsync(args, toolCall, CancellationToken.None);
        var subAgent = toolCall.SubAgent!;

        await WaitUntilAsync(() => subAgent.PendingToolCallId == "tc1");
        await subAgent.ToolCallHandler!.HandleApprovalAsync("tc1", approved: false);

        var result = await runTask;

        // Assert — rejected tool never executes, loop continues to final answer
        Assert.False(toolExecuted.Task.IsCompleted, "Rejected tool must not execute");
        Assert.True(result.Success);
        Assert.Equal("Final answer", result.Result);
        var llmToolCall = llmToolCallAccessor();
        Assert.NotNull(llmToolCall!.Result); // denied result attached to the tool call
        Assert.Equal(ToolApprovalStatus.Rejected, llmToolCall.ApprovalStatus);
        Assert.Equal(SubAgentStatus.Completed, subAgent.Status);
    }

    [Fact]
    public async Task Approval_UnknownIdOnSubAgentHandler_IsNoOp()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "T", systemPrompt = "P" });

        var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetupApprovalScenario("slow_tool", releaseTool, toolExecuted);

        // Act
        var runTask = _executor.ExecuteAsync(args, toolCall, CancellationToken.None);
        var subAgent = toolCall.SubAgent!;
        await WaitUntilAsync(() => subAgent.PendingToolCallId == "tc1");

        // Assert — the sub-agent has its own handler instance (isolation by construction),
        // and approving an unknown id is a silent no-op that does not unblock the waiter.
        Assert.NotNull(subAgent.ToolCallHandler);
        await subAgent.ToolCallHandler.HandleApprovalAsync("unknown-id", approved: true);

        Assert.False(toolExecuted.Task.IsCompleted, "Unknown-id approval must not execute the tool");

        releaseTool.TrySetResult();
        await subAgent.ToolCallHandler.HandleApprovalAsync("tc1", approved: true);
        var result = await runTask;
        Assert.True(result.Success);
    }
}
