using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace UIBlazor.Tests.Services;

/// <summary>
/// TokensToCompress in sub-agent branches: before every LLM attempt
/// <see cref="AfSubAgentOrchestrator"/> checks profile.TokensToCompress &gt; 0 &amp;&amp;
/// session.TotalTokens &gt; tokensToCompress and calls
/// chatService.CompressSessionAsync(session, ...) with subAgent.IsCompressing = true/false
/// around the call. tokensToCompress = 0 disables the check.
/// Compression can only fire before a RETRY attempt (attempt &gt;= 2): before the first
/// attempt the branch session is empty (TotalTokens = 0, the user message carries no
/// tokens) - tokens are accumulated by the streamed response, so the stub fails the
/// first call only AFTER the stream has been consumed.
/// </summary>
public class AfSubAgentOrchestratorCompressionTests
{
    // "Hello world!" = 12 chars -> >= 3 estimated tokens (~4 chars/token) in any chunking,
    // so tokensToCompress = 1 is always exceeded while the branch itself completes successfully.
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
    private readonly StubChatService _chatService = new();

    public AfSubAgentOrchestratorCompressionTests()
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

    private AfSubAgentOrchestrator CreateOrchestrator(int tokensToCompress, int maxRetries = 3)
    {
        _profileManager.SetupGet(p => p.ActiveProfile).Returns(new ConnectionProfile
        {
            Endpoint = _server.Urls[0],
            ApiKey = "key",
            Model = "test-model",
            TokensToCompress = tokensToCompress
        });

        // MaxRetries = 3 (default): the stub's failed first attempt is retried, so the
        // compression check re-runs before attempt 2 with the tokens attempt 1 accumulated.
        _commonSettings.SetupGet(s => s.Current).Returns(new CommonOptions { MaxRetries = maxRetries });
        _retryHandler
            .Setup(r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new AfSubAgentOrchestrator(
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _chatService,
            _contentFilter.Object,
            new HttpClient(),
            _commonSettings.Object,
            _retryHandler.Object,
            NullLogger<AfSubAgentOrchestrator>.Instance);
    }

    private static ToolCall CreateDelegateTaskCall() => new()
    {
        Id = "tc-1",
        Function = new ToolCallFunction { Name = BuiltInToolEnum.DelegateTask }
    };

    private const string DelegateTaskArgs = """{"tasks":[{"task":"do it","systemPrompt":"sp","allowedTools":[]}]}""";

    [Fact(Timeout = 20000)]
    public async Task ExecuteAsync_TokensToCompressExceeded_CompressesSubAgentSession()
    {
        // Attempt 1 streams the response (tokens accumulate) and then fails transiently;
        // before the retry the compression check re-runs with TotalTokens > tokensToCompress.
        _chatService.FailFirstNCalls = 1;
        var orchestrator = CreateOrchestrator(tokensToCompress: 1);
        var toolCall = CreateDelegateTaskCall();

        var result = await orchestrator.ExecuteAsync(
            DelegateTaskArgs,
            toolCall,
            TestContext.Current.CancellationToken);

        // The branch itself completes successfully - compression is transparent to the result.
        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
        Assert.True(result.Success);
        Assert.Contains("Hello world!", result.Result);

        // The compress overload received the branch session (Id starts with "subagent_").
        Assert.True(_chatService.CompressCalled);
        var compressedId = Assert.Single(_chatService.CompressedSessionIds);
        Assert.StartsWith("subagent_", compressedId);
    }

    [Fact(Timeout = 20000)]
    public async Task ExecuteAsync_TokensToCompressDisabled_NoCompression()
    {
        // Same transient failure: tokens exceed the (disabled) threshold before attempt 2,
        // yet compression must never be called when tokensToCompress = 0.
        _chatService.FailFirstNCalls = 1;
        var orchestrator = CreateOrchestrator(tokensToCompress: 0);
        var toolCall = CreateDelegateTaskCall();

        var result = await orchestrator.ExecuteAsync(
            DelegateTaskArgs,
            toolCall,
            TestContext.Current.CancellationToken);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
        Assert.True(result.Success);

        Assert.False(_chatService.CompressCalled);
        Assert.Empty(_chatService.CompressedSessionIds);
    }

    [Fact(Timeout = 20000)]
    public async Task ExecuteAsync_Compression_SetsIsCompressingDuringCallOnly()
    {
        // Compression fires before the retry attempt (attempt 2), not before attempt 1.
        _chatService.FailFirstNCalls = 1;
        var orchestrator = CreateOrchestrator(tokensToCompress: 1);
        var toolCall = CreateDelegateTaskCall();
        // The stub learns the branch's SubAgentMessage from the tool call itself
        // (SubAgents are populated in CreateTickets before the branch runs).
        _chatService.SubAgentProvider = () => toolCall.SubAgents?.FirstOrDefault();

        var result = await orchestrator.ExecuteAsync(
            DelegateTaskArgs,
            toolCall,
            TestContext.Current.CancellationToken);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
        Assert.True(result.Success);

        // IsCompressing was true while CompressSessionAsync was running...
        Assert.True(_chatService.CompressCalled);
        Assert.True(_chatService.IsCompressingDuringCall);

        // ...and is reset back to false after the call.
        Assert.False(branch.IsCompressing);
    }

    /// <summary>
    /// Minimal IChatService stub: only ProcessStreamAsync is used by the orchestrator
    /// (it pipes branch deltas into the SubAgent streaming message). The sub-agent
    /// CompressSessionAsync overload records the target session and the IsCompressing
    /// flag observed during the call.
    /// </summary>
    private sealed class StubChatService : IChatService
    {
        public ConversationSession Session { get; } = new();

        public bool NeedCompression => false;

        public bool CompressCalled;
        public List<string> CompressedSessionIds { get; } = [];
        public Func<SubAgentMessage?>? SubAgentProvider;
        public bool? IsCompressingDuringCall;

        // Transient-failure simulation: the first N invocations consume the WHOLE stream
        // (tokens accumulate into the branch session) and then throw, so the orchestrator
        // retries and its compression check re-runs before attempt 2 with real tokens.
        public int FailFirstNCalls;
        public int InvocationCount;

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
            var invocation = ++InvocationCount;
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

            // Fail AFTER the stream is consumed: the retry attempt must see the tokens this
            // run accumulated into the session, otherwise the compression check (which runs
            // before every attempt) can never fire - before attempt 1 the session is empty.
            if (invocation <= FailFirstNCalls)
                throw new InvalidOperationException("transient failure");

            message.IsStreaming = false;
        }

        public IAsyncEnumerable<ChatDelta> GetCompletionsAsync(CompletionsResult resultCapture, CancellationToken cancellationToken)
            => EmptyDeltas();

        public IAsyncEnumerable<ChatDelta> CompressSessionAsync(CompletionsResult resultCapture, CancellationToken cancellationToken)
            => EmptyDeltas();

        public IAsyncEnumerable<ChatDelta> CompressSessionAsync(ConversationSession targetSession, CompletionsResult resultCapture, CancellationToken cancellationToken)
        {
            CompressCalled = true;
            CompressedSessionIds.Add(targetSession.Id);
            IsCompressingDuringCall = SubAgentProvider?.Invoke()?.IsCompressing;
            return EmptyDeltas();
        }

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
