using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Regression: branch text produced by AfSubAgentOrchestrator.RunBranchAsync must reach
/// the delegate_task VsToolResult (ticket.ResultText). The branch return value was
/// previously discarded (Task.WhenAll without reading results), so the model always
/// received "(Sub-agent returned an empty response.)" instead of the branch answer.
/// </summary>
public class AfSubAgentOrchestratorResultTests
{
    private readonly Mock<IProfileManager> _profileManager = new();
    private readonly Mock<ISystemPromptBuilder> _promptBuilder = new();
    private readonly Mock<IToolManager> _toolManager = new();
    private readonly Mock<IContentFilter> _contentFilter = new();
    private readonly Mock<ICommonSettingsProvider> _commonSettings = new();
    private readonly Mock<IRetryHandler> _retryHandler = new();
    private readonly WireMockServer _server = WireMockServer.Start();

    public AfSubAgentOrchestratorResultTests()
    {
        _server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("no model"));

        _profileManager.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = _server.Urls[0],
            ApiKey = "key",
            Model = "test-model"
        });
        _promptBuilder
            .Setup(b => b.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sub prompt");
        _toolManager.Setup(t => t.GetEnabledTools(AppMode.Agent)).Returns([]);

        // MaxRetries = 1: no retries, the first failure rethrows (previous behavior).
        _commonSettings.SetupGet(s => s.Current).Returns(new CommonOptions { MaxRetries = 1 });
        _retryHandler
            .Setup(r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private AfSubAgentOrchestrator CreateOrchestrator()
        => new(
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            new StubChatService(),
            _contentFilter.Object,
            new HttpClient(),
            _commonSettings.Object,
            _retryHandler.Object,
            NullLogger<AfSubAgentOrchestrator>.Instance);

    [Fact]
    public async Task ExecuteAsync_BranchFails_ResultTextReachesToolResult()
    {
        var orchestrator = CreateOrchestrator();
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        var result = await orchestrator.ExecuteAsync(
            """{"tasks":[{"task":"do it","systemPrompt":"sp","allowedTools":[]}]}""",
            toolCall,
            TestContext.Current.CancellationToken);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Failed, branch.Status);
        Assert.NotEmpty(branch.ErrorMessage!);

        // The failing branch's text is part of the aggregated tool result
        // (it was an empty string before the ResultText fix).
        Assert.Contains("(Sub-agent branch failed", result.Result);
        Assert.False(result.Success);
        Assert.Equal("One or more delegated task(s) failed.", result.ErrorMessage);
    }

    /// <summary>
    /// Minimal IChatService stub: only ProcessStreamAsync is used by the orchestrator
    /// (it pipes branch deltas into the SubAgent streaming message).
    /// </summary>
    private sealed class StubChatService : IChatService
    {
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
            var content = new StringBuilder();
            await foreach (var delta in deltas.WithCancellation(cancellationToken))
            {
                if (delta.IsStartOfTurn && onNewTurn is not null && message.IsStreaming)
                {
                    message.IsStreaming = false;
                    message = onNewTurn();
                }

                if (delta.Content is not null)
                {
                    content.Append(delta.Content);
                    message.Content = content.ToString();
                    onContentUpdate?.Invoke(delta.Content);
                }

                if (delta.ToolCalls is { Count: > 0 } && resultCapture.AccumulatedToolCalls is not null)
                {
                    onToolCallsUpdate?.Invoke(resultCapture.AccumulatedToolCalls);
                }

                onStateChange?.Invoke();
            }

            message.IsStreaming = false;
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
