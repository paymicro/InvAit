using Microsoft.Extensions.Logging.Abstractions;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Retries inside a sub-agent branch: IChatService.ProcessStreamAsync throws on the first
/// attempts and succeeds on the last one. The branch must complete successfully, the retry
/// handler must be consulted (GetRetryDelay/WaitForRetryAsync), and the final answer must
/// reach the delegate_task result. When the retry budget is exhausted, the last exception
/// escapes the retry loop and the branch fails (previous single-attempt behavior).
/// </summary>
public class AfSubAgentOrchestratorRetryTests
{
    private readonly Mock<IProfileManager> _profileManager = new();
    private readonly Mock<ISystemPromptBuilder> _promptBuilder = new();
    private readonly Mock<IToolManager> _toolManager = new();
    private readonly Mock<IContentFilter> _contentFilter = new();
    private readonly Mock<ICommonSettingsProvider> _commonSettings = new();
    private readonly Mock<IRetryHandler> _retryHandler = new();

    public AfSubAgentOrchestratorRetryTests()
    {
        // No HTTP call is made: the stub chat service throws before enumerating deltas.
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

        _commonSettings.SetupGet(s => s.Current).Returns(new CommonOptions { MaxRetries = 3 });

        _retryHandler.Setup(r => r.GetRetryDelay(It.IsAny<int>())).Returns(0);
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

    private static ToolCall CreateToolCall()
        => new()
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

    private const string Args = """{"tasks":[{"task":"do it","systemPrompt":"sp","allowedTools":[]}]}""";

    [Fact]
    public async Task ExecuteAsync_TransientFailures_RetriedAndBranchSucceeds()
    {
        var chatService = new FailingThenSuccessChatService(failures: 2);
        var orchestrator = CreateOrchestrator(chatService);
        var toolCall = CreateToolCall();

        var result = await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        // Two transient failures + one success = three calls.
        Assert.Equal(3, chatService.Calls);

        // The retry handler was consulted for each failed attempt.
        _retryHandler.Verify(r => r.GetRetryDelay(1), Times.Once);
        _retryHandler.Verify(r => r.GetRetryDelay(2), Times.Once);
        _retryHandler.Verify(
            r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // The branch completed successfully and the answer reached the tool result.
        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
        Assert.True(result.Success);
        Assert.Contains("sub-agent answer", result.Result);

        // Retry badges were armed during the failures and reset after the wait.
        Assert.False(branch.IsRetrying);
        Assert.Equal(0, branch.RetryCountdown);
        Assert.Equal(3, branch.MaxRetryAttempts);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesExhausted_BranchFails()
    {
        var chatService = new FailingThenSuccessChatService(failures: 10);
        var orchestrator = CreateOrchestrator(chatService);
        var toolCall = CreateToolCall();

        var result = await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        // MaxRetries = 3: three attempts, then the last exception escapes the retry loop.
        Assert.Equal(3, chatService.Calls);
        _retryHandler.Verify(
            r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Failed, branch.Status);
        Assert.NotEmpty(branch.ErrorMessage!);
        Assert.False(result.Success);
    }

    /// <summary>
    /// IChatService stub: throws on the first <c>failures</c> calls, then completes with an
    /// answer. Only ProcessStreamAsync is used by the orchestrator.
    /// </summary>
    private sealed class FailingThenSuccessChatService : IChatService
    {
        private readonly int _failures;

        public FailingThenSuccessChatService(int failures) => _failures = failures;

        public int Calls { get; private set; }

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
            Calls++;
            if (Calls <= _failures)
                throw new InvalidOperationException("transient failure");

            message.Content = "sub-agent answer";
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
