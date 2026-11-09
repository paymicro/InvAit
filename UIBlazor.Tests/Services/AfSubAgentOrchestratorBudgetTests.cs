using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace UIBlazor.Tests.Services;

/// <summary>
/// MaxTokensPerSubAgent budget: a branch that exceeds the budget (session.TotalTokens &gt; budget)
/// still reports its answer, but the delegate_task result carries a
/// "(Token budget exceeded: ...)" warning for the parent agent. budget = 0 disables the check.
/// </summary>
public class AfSubAgentOrchestratorBudgetTests
{
    // "Hello world!" = 12 chars -> >= 3 estimated tokens (~4 chars/token) in any chunking,
    // so budget = 1 is always exceeded while the branch itself completes successfully.
    private const string SseResponse = """
        data: {"id":"d9d528e22108450d96716ce5f36fb2ea","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"role":"assistant","content":"Hello","reasoning_content":null,"tool_calls":null},"finish_reason":null}],"usage":null}

        data: {"id":"d9d528e22108450d96716ce5f36fb2ea","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"role":null,"content":" world!","reasoning_content":null,"tool_calls":null},"finish_reason":null}],"usage":null}

        data: {"id":"d9d528e22108450d96716ce5f36fb2ea","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{},"finish_reason":"stop"}],"usage":null}

        data: [DONE]

        """;

    private readonly Mock<IProfileManager> _profileManager = new();
    private readonly Mock<ISystemPromptBuilder> _promptBuilder = new();
    private readonly Mock<IToolManager> _toolManager = new();
    private readonly Mock<IContentFilter> _contentFilter = new();
    private readonly Mock<ICommonSettingsProvider> _commonSettings = new();
    private readonly Mock<IRetryHandler> _retryHandler = new();
    private readonly WireMockServer _server = WireMockServer.Start();

    public AfSubAgentOrchestratorBudgetTests()
    {
        _server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithHeader("Cache-Control", "no-cache")
                .WithBody(SseResponse));

        _promptBuilder
            .Setup(b => b.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sub prompt");
        _toolManager.Setup(t => t.GetEnabledTools(AppMode.Agent)).Returns([]);
    }

    private AfSubAgentOrchestrator CreateOrchestrator(int maxTokensPerSubAgent)
    {
        _profileManager.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = _server.Urls[0],
            ApiKey = "key",
            Model = "test-model",
            MaxTokensPerSubAgent = maxTokensPerSubAgent
        });

        // MaxRetries = 1: no retries, the first failure rethrows (previous behavior).
        _commonSettings.SetupGet(s => s.Current).Returns(new CommonOptions { MaxRetries = 1 });
        _retryHandler
            .Setup(r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new AfSubAgentOrchestrator(
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            new StubChatService(),
            _contentFilter.Object,
            new HttpClient(),
            _commonSettings.Object,
            _retryHandler.Object,
            NullLogger<AfSubAgentOrchestrator>.Instance);
    }

    [Fact(Timeout = 20000)]
    public async Task ExecuteAsync_OverBudget_AppendsWarningToResult()
    {
        var orchestrator = CreateOrchestrator(maxTokensPerSubAgent: 1);
        var toolCall = new ToolCall
        {
            Id = "tc-1",
            Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
        };

        var result = await orchestrator.ExecuteAsync(
            """{"tasks":[{"task":"do it","systemPrompt":"sp","allowedTools":[]}]}""",
            toolCall,
            TestContext.Current.CancellationToken);

        // The branch itself completes successfully - the warning is appended, not a failure.
        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
        Assert.True(result.Success);

        // The answer is still reported, with the budget warning appended for the parent agent.
        Assert.Contains("Hello world!", result.Result);
        Assert.Contains("Token budget exceeded", result.Result);
    }

    [Fact(Timeout = 20000)]
    public async Task ExecuteAsync_BudgetDisabled_NoWarning()
    {
        var orchestrator = CreateOrchestrator(maxTokensPerSubAgent: 0);
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
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
        Assert.True(result.Success);

        Assert.Contains("Hello world!", result.Result);
        Assert.DoesNotContain("Token budget exceeded", result.Result);
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
