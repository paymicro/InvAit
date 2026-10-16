using Microsoft.Extensions.Logging.Abstractions;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Regression: reasoning-only deltas (no Content, no ToolCalls) must reach the sub-agent's
/// UI event stream. ChatService.ProcessStreamAsync reports them via onStateChange ONLY —
/// previously the orchestrator's onStateChange callback did not call NotifyStateChanged,
/// so SubAgentView.HasChanges() saw no Revision bump and the thinking block never
/// re-rendered while streaming.
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
        // Arrange
        var chatService = new ReasoningOnlyChatService();
        var orchestrator = CreateOrchestrator(chatService);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        // Grab the branch reference as soon as the orchestrator attaches it,
        // and arm the revision probe used by the stub during streaming.
        SubAgentMessage? branch = null;
        toolCall.SubAgentsChanged += () =>
        {
            branch = toolCall.SubAgents![0];
            chatService.ProbeRevision = () => branch!.Revision;
        };

        // Act
        await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        // Assert - the reasoning-only delta (onStateChange, no onContentUpdate)
        // bumped the branch Revision: SubAgentView will re-render the thinking block.
        Assert.NotNull(branch);
        Assert.True(chatService.SawReasoningDelta, "stub must stream a reasoning-only delta");
        Assert.True(
            chatService.RevisionAfterReasoning > chatService.RevisionBeforeReasoning,
            $"reasoning-only delta must bump the branch Revision " +
            $"(before: {chatService.RevisionBeforeReasoning}, after: {chatService.RevisionAfterReasoning})");

        // The branch completed normally.
        Assert.Equal(SubAgentStatus.Completed, branch!.Status);
    }

    [Fact]
    public async Task ExecuteAsync_ReasoningDelta_MarksMessageForRender()
    {
        // The same reasoning delta must also set IsShouldRender on the streaming
        // message (the throttled content-update path in SubAgentView).
        var chatService = new ReasoningOnlyChatService();
        var orchestrator = CreateOrchestrator(chatService);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        toolCall.SubAgentsChanged += () =>
        {
            var branch = toolCall.SubAgents![0];
            chatService.ProbeShouldRender = () => branch.GetMessages().Any(m => m.IsShouldRender);
        };

        await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        Assert.True(
            chatService.ShouldRenderAfterReasoning,
            "reasoning-only delta must set IsShouldRender on the streaming message");
    }

    /// <summary>
    /// IChatService stub: streams a reasoning-only delta (onStateChange fires,
    /// onContentUpdate does NOT) and then a content delta. Probes capture the branch
    /// state around the reasoning delta to verify the UI event stream was reached.
    /// </summary>
    private sealed class ReasoningOnlyChatService : IChatService
    {
        public Func<long>? ProbeRevision { get; set; }
        public Func<bool>? ProbeShouldRender { get; set; }

        public bool SawReasoningDelta { get; private set; }
        public long RevisionBeforeReasoning { get; private set; }
        public long RevisionAfterReasoning { get; private set; }
        public bool ShouldRenderAfterReasoning { get; private set; }

        public ConversationSession Session { get; } = new();

        public bool NeedCompression => false;

        public event PropertyChangedEventHandler? SessionChanged;
        public void Initialize() { }
        public ConnectionProfile? Options => null;

        public Task ProcessStreamAsync(
            VisualChatMessage message,
            IAsyncEnumerable<ChatDelta> deltas,
            Action<string>? onContentUpdate,
            Action<List<ToolCall>>? onToolCallsUpdate,
            Action? onStateChange,
            CompletionsResult resultCapture,
            CancellationToken cancellationToken)
            => ProcessStreamAsync(message, deltas, onContentUpdate, onToolCallsUpdate, onStateChange,
                resultCapture, cancellationToken, onNewTurn: null);

        public async Task ProcessStreamAsync(
            VisualChatMessage message,
            IAsyncEnumerable<ChatDelta> deltas,
            Action<string>? onContentUpdate,
            Action<List<ToolCall>>? onToolCallsUpdate,
            Action? onStateChange,
            CompletionsResult resultCapture,
            CancellationToken cancellationToken,
            Func<VisualChatMessage>? onNewTurn)
        {
            // Reasoning-only delta: ChatService reports it via onStateChange ONLY.
            SawReasoningDelta = true;
            RevisionBeforeReasoning = ProbeRevision?.Invoke() ?? -1;
            onStateChange?.Invoke();
            RevisionAfterReasoning = ProbeRevision?.Invoke() ?? -1;
            ShouldRenderAfterReasoning = ProbeShouldRender?.Invoke() ?? false;

            // Content delta: normal path (onContentUpdate fires).
            message.Content = "sub-agent answer";
            onContentUpdate?.Invoke("sub-agent answer");

            message.IsStreaming = false;
            await Task.CompletedTask;
        }

        public IAsyncEnumerable<ChatDelta> GetCompletionsAsync(CompletionsResult resultCapture, CancellationToken cancellationToken)
            => EmptyDeltas();

        public IAsyncEnumerable<ChatDelta> CompressSessionAsync(CompletionsResult resultCapture, CancellationToken cancellationToken)
            => EmptyDeltas();

        public IAsyncEnumerable<ChatDelta> CompressSessionAsync(ConversationSession targetSession, CompletionsResult resultCapture, CancellationToken cancellationToken)
            => EmptyDeltas();

        public Task<List<SessionSummary>> GetRecentSessionsAsync(int count) => Task.FromResult(new List<SessionSummary>());
        public Task LoadSessionAsync(string id) => Task.CompletedTask;
        public Task DeleteSessionAsync(string id) => Task.CompletedTask;
        public Task SaveSessionAsync() => Task.CompletedTask;
        public Task NewSessionAsync() => Task.CompletedTask;
        public Task<AiModelList> GetModelsAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();

        private static async IAsyncEnumerable<ChatDelta> EmptyDeltas()
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose() { }
    }
}
