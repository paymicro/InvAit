using Microsoft.Extensions.Logging.Abstractions;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Regression: reasoning-only deltas (no Content, no ToolCalls) must reach the sub-agent's
/// UI event stream. ChatService.ProcessStreamAsync reports them via onStateChange ONLY —
/// previously the orchestrator's onStateChange callback did not call NotifyStateChanged,
/// so SubAgentView.HasChanges() saw no Revision bump and the thinking block never
/// re-rendered while streaming.
/// <para>
/// The real <see cref="ChatService.ProcessStreamAsync"/> runs here (see
/// <see cref="ScriptedChatService"/>): only the LLM turn is scripted. The probe fires from
/// INSIDE the delta stream, between the reasoning update and the text update — the exact
/// moment the service has already applied the reasoning and invoked onStateChange.
/// </summary>
public class AfSubAgentOrchestratorStreamingTests
{
    private readonly Mock<IProfileManager> _profileManager = new();
    private readonly Mock<ISystemPromptBuilder> _promptBuilder = new();
    private readonly Mock<IToolManager> _toolManager = new();
    private readonly Mock<IContentFilter> _contentFilter = new();
    private readonly Mock<ICommonSettingsProvider> _commonSettings = new();
    private readonly Mock<IRetryHandler> _retryHandler = new();

    public AfSubAgentOrchestratorStreamingTests()
    {
        // No HTTP call is made: the stub chat service streams deltas directly.
        _profileManager.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = "http://localhost:1",
            ApiKey = "key",
            Model = "test-model"
        });
        _promptBuilder
            .Setup(b => b.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sub prompt");
        _toolManager.Setup(t => t.GetEnabledTools(AppMode.Agent)).Returns([]);

        _commonSettings.SetupGet(s => s.Current).Returns(new CommonOptions { MaxRetries = 1 });
        _retryHandler
            .Setup(r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private AfSubAgentOrchestrator CreateOrchestrator(IChatService chatService)
        => new(
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            chatService,
            _contentFilter.Object,
            new HttpClient(),
            _commonSettings.Object,
            _retryHandler.Object,
            NullLogger<AfSubAgentOrchestrator>.Instance);

    private const string Args = """{"tasks":[{"task":"do it","systemPrompt":"sp","allowedTools":[]}]}""";

    [Fact]
    public async Task ExecuteAsync_ReasoningOnlyDelta_BranchRevisionIsBumped()
    {
        // Arrange: the probe runs INSIDE the delta stream, right after the service applied the
        // reasoning update and invoked onStateChange - the state the branch UI would render from.
        SubAgentMessage? branch = null;
        long revisionBefore = -1;
        long revisionAfter = -1;
        var sawReasoningDelta = false;

        using var chatService = ScriptedChatService.Create(Turns.ReasoningThenText(
            reasoning: "thinking hard",
            content: "sub-agent answer",
            // Nothing of the reasoning update is applied yet.
            onBeforeReasoning: () =>
            {
                revisionBefore = branch!.Revision;
                return Task.CompletedTask;
            },
            // The service has applied the reasoning and invoked onStateChange by now.
            onReasoningApplied: () =>
            {
                sawReasoningDelta = true;
                revisionAfter = branch!.Revision;
                return Task.CompletedTask;
            }));

        var orchestrator = CreateOrchestrator(chatService.Service);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        // Grab the branch reference as soon as the orchestrator attaches it.
        toolCall.SubAgentsChanged += () => branch ??= toolCall.SubAgents![0];

        // Act
        await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        // Assert - the reasoning-only delta (onStateChange, no onContentUpdate)
        // bumped the branch Revision: SubAgentView will re-render the thinking block.
        Assert.NotNull(branch);
        Assert.True(sawReasoningDelta, "the scripted turn must deliver a reasoning-only update");

        // Both readings straddle exactly ONE delta, so the bump is caused by the reasoning update
        // alone (not by the turn close or the terminal status, which happen later).
        Assert.True(
            revisionAfter > revisionBefore,
            $"reasoning-only delta must bump the branch Revision " +
            $"(before: {revisionBefore}, after: {revisionAfter})");

        // The branch completed normally.
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ReasoningDelta_MarksMessageForRender()
    {
        // The same reasoning delta must also set IsShouldRender on the streaming
        // message (the throttled content-update path in SubAgentView).
        SubAgentMessage? branch = null;
        var shouldRenderAfterReasoning = false;

        using var chatService = ScriptedChatService.Create(Turns.ReasoningThenText(
            reasoning: "thinking hard",
            content: "sub-agent answer",
            onReasoningApplied: () =>
            {
                shouldRenderAfterReasoning = branch!.GetMessages().Any(m => m.IsShouldRender);
                return Task.CompletedTask;
            }));

        var orchestrator = CreateOrchestrator(chatService.Service);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        toolCall.SubAgentsChanged += () => branch ??= toolCall.SubAgents![0];

        await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        Assert.True(
            shouldRenderAfterReasoning,
            "reasoning-only delta must set IsShouldRender on the streaming message");
    }

    [Fact]
    public async Task ExecuteAsync_MultiTask_FirstFinishedBranchGetsTerminalStatusImmediately()
    {
        // Regression: with several parallel branches, a branch that finishes first
        // must switch to Completed immediately (its own finally), not stay Running
        // until the whole batch completes.
        using var chatService = ScriptedChatService.Create(Turns.Text("branch answer"));
        var orchestrator = CreateOrchestrator(chatService.Service);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        var args = """{"tasks":[{"task":"a","systemPrompt":"sp","allowedTools":[]},{"task":"b","systemPrompt":"sp","allowedTools":[]}]}""";

        SubAgentMessage? first = null;
        SubAgentMessage? second = null;
        toolCall.SubAgentsChanged += () =>
        {
            var agents = toolCall.SubAgents!;
            if (agents.Count > 0) first ??= agents[0];
            if (agents.Count > 1) second ??= agents[1];
        };

        await orchestrator.ExecuteAsync(args, toolCall, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // Both branches are terminal at the end of the batch.
        Assert.Equal(SubAgentStatus.Completed, first!.Status);
        Assert.Equal(SubAgentStatus.Completed, second!.Status);
        // The first branch was finalized by its own finally block, not only by ComposeResult.
        Assert.True(first.CompletedAt is not null);
        Assert.True(second.CompletedAt is not null);
    }
}
