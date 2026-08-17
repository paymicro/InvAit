namespace UIBlazor.Tests.Agents;

public partial class SubAgentExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_FiresSubAgentStateChanged_OnInitialNotification()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        var eventCallCount = 0;
        SubAgentMessage? receivedSubAgent = null;
        _executor.SubAgentStateChanged += subAgent =>
        {
            eventCallCount++;
            receivedSubAgent = subAgent;
        };

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - at least the initial notification should fire
        Assert.True(eventCallCount >= 1);
        Assert.NotNull(receivedSubAgent);
        Assert.Equal("Test", receivedSubAgent!.Task);
    }

    [Fact]
    public async Task ExecuteAsync_FiresSubAgentStateChanged_OnCompletion()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        var statuses = new List<SubAgentStatus>();
        _executor.SubAgentStateChanged += subAgent =>
        {
            statuses.Add(subAgent.Status);
        };

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - the last notification should have Completed status
        Assert.Contains(SubAgentStatus.Completed, statuses);
        Assert.Equal(SubAgentStatus.Completed, statuses[^1]);
    }

    [Fact]
    public async Task ExecuteAsync_FiresSubAgentStateChanged_OnCancellation()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        var cts = new CancellationTokenSource();
        SetupChatServiceToThrowCancellation(cts.Token);

        var statuses = new List<SubAgentStatus>();
        _executor.SubAgentStateChanged += subAgent =>
        {
            statuses.Add(subAgent.Status);
        };

        // Act
        await _executor.ExecuteAsync(args, toolCall, cts.Token);

        // Assert - the last notification should have Cancelled status
        Assert.Contains(SubAgentStatus.Cancelled, statuses);
        Assert.Equal(SubAgentStatus.Cancelled, statuses[^1]);
    }

    [Fact]
    public async Task ExecuteAsync_FiresSubAgentStateChanged_OnFailure()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToThrowException(new InvalidOperationException("LLM error"));

        var statuses = new List<SubAgentStatus>();
        _executor.SubAgentStateChanged += subAgent =>
        {
            statuses.Add(subAgent.Status);
        };

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - the last notification should have Failed status
        Assert.Contains(SubAgentStatus.Failed, statuses);
        Assert.Equal(SubAgentStatus.Failed, statuses[^1]);
    }

    [Fact]
    public async Task ExecuteAsync_NoSubAgentStateChanged_WhenNoSubscriber()
    {
        // Arrange - no subscriber attached, should not throw
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        // Act - should complete without NullReferenceException
        var result = await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(toolCall.SubAgent);
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentStateChanged_FiresMultipleTimesDuringToolLoop()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });

        var completionsCallCount = 0;
        var processStreamCallCount = 0;
        var eventCount = 0;

        _executor.SubAgentStateChanged += _ => eventCount++;

        _chatServiceMock
            .Setup(x => x.GetCompletionsForSubAgentAsync(
                It.IsAny<ConversationSession>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<Tool>>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, string, IEnumerable<Tool>, CompletionsResult, CancellationToken>(
                (_, _, _, resultCapture, _) =>
                {
                    completionsCallCount++;
                    resultCapture.Model = "test-model";
                    resultCapture.AccumulatedToolCalls = completionsCallCount == 1
                        ? [new ToolCall { Id = "tc1", Function = new ToolCallFunction { Name = "read_files", Arguments = "{}" } }]
                        : null;
                })
            .Returns(CreateEmptyDeltaStream());

        _chatServiceMock
            .Setup(x => x.ProcessStreamAsync(
                It.IsAny<VisualChatMessage>(),
                It.IsAny<IAsyncEnumerable<ChatDelta>>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<List<ToolCall>>>(),
                It.IsAny<Action?>(),
                It.IsAny<CompletionsResult>(),
                It.IsAny<CancellationToken>()))
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

        _toolManagerMock.Setup(x => x.GetEnabledTools(AppMode.Agent)).Returns(new List<Tool>
        {
            CreateTool(BuiltInToolEnum.ReadFiles)
        });

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - should fire at least: initial + completion = 2 (plus any internal notifications)
        Assert.True(eventCount >= 2, $"Expected at least 2 events, got {eventCount}");
    }

    [Fact]
    public async Task ExecuteAsync_SubAgentStateChanged_InitialEventHasRunningStatus()
    {
        // Arrange
        var toolCall = new ToolCall();
        var args = JsonSerializer.Serialize(new { task = "Test", systemPrompt = "Prompt" });
        SetupChatServiceToReturnContent("Done!");

        SubAgentStatus? firstStatus = null;
        _executor.SubAgentStateChanged += subAgent =>
        {
            if (firstStatus is null)
                firstStatus = subAgent.Status;
        };

        // Act
        await _executor.ExecuteAsync(args, toolCall, CancellationToken.None);

        // Assert - the first event should have Running status
        Assert.Equal(SubAgentStatus.Running, firstStatus);
    }
}
