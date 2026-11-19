using System.Runtime.CompilerServices;
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
/// Compression can only fire before a TURN after the first one: before turn 1 the branch
/// session is empty (TotalTokens = 0, the user message carries no tokens), tokens are
/// accumulated by the streamed response. The WireMock script therefore answers turn 1 with
/// a tool call (forcing a second turn) and every later turn with plain text.
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

    // Turn 1 answers with text + a tool call (unknown tool: the execution produces an error
    // result, which is all the loop needs - a Result for every call, then a second LLM turn).
    // The text is what accumulates tokens (> 1 estimated token) into the branch session.
    private const string ToolCallSseResponse = """
        data: {"id":"a1","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"role":"assistant","content":"Let me read the file first, then continue.","reasoning_content":null,"tool_calls":null},"finish_reason":null}],"usage":null}

        data: {"id":"a1","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{"role":null,"content":null,"reasoning_content":null,"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_files","arguments":"{}"}}]},"finish_reason":null}],"usage":null}

        data: {"id":"a1","object":"chat.completion.chunk","created":1773130988,"model":"zai-org/GLM-5","choices":[{"index":0,"message":null,"delta":{},"finish_reason":"tool_calls"}],"usage":null}

        data: [DONE]

        """;

    private readonly Mock<IProfileManager> _profileManager = new();
    private readonly Mock<ISystemPromptBuilder> _promptBuilder = new();
    private readonly Mock<IToolManager> _toolManager = new();
    private readonly Mock<IContentFilter> _contentFilter = new();
    private readonly Mock<ICommonSettingsProvider> _commonSettings = new();
    private readonly Mock<IRetryHandler> _retryHandler = new();
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly RecordingChatService _chatService;

    public AfSubAgentOrchestratorCompressionTests()
    {
        // Two-turn script: turn 1 returns a tool call, every later turn returns plain text.
        // The compression gate is checked BEFORE each turn, so it can only fire before turn 2 -
        // turn 1 is what accumulates the tokens of the branch session.
        _server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .InScenario("turns")
            .WillSetStateTo("turn2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithHeader("Cache-Control", "no-cache")
                .WithBody(ToolCallSseResponse));

        _server
            .Given(Request.Create().WithPath("/v1/chat/completions").UsingPost())
            .InScenario("turns")
            .WhenStateIs("turn2")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "text/event-stream")
                .WithHeader("Cache-Control", "no-cache")
                .WithBody(SseResponse));

        _promptBuilder
            .Setup(b => b.PrepareSubAgentSystemPromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sub prompt");
        _toolManager.Setup(t => t.GetEnabledTools(AppMode.Agent)).Returns([]);

        // REAL ChatService + its own AfCompletionsAdapter against the WireMock endpoint: the turns
        // accumulate real provider tokens into the branch session (the compression gate reads
        // session.TotalTokens), and ProcessStreamAsync is the production one. Only the compression
        // CALLS are recorded - see RecordingChatService.
        _realService = ScriptedChatService.CreateWithRealAdapter(
            profileManager: _profileManager.Object,
            commonSettings: _commonSettings.Object);
        _chatService = new RecordingChatService(_realService.Service);
    }

    private readonly ScriptedChatService _realService;

    private AfSubAgentOrchestrator CreateOrchestrator(int tokensToCompress, int maxRetries = 3)
    {
        // The SAME manager feeds the orchestrator's gate and the real service's adapter.
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
        // Turn 1 accumulates tokens, the gate re-runs before turn 2 with
        // TotalTokens > tokensToCompress and must compress the branch session.
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
        Assert.True(_chatService.CompressCalls > 0);
        var compressedId = Assert.Single(_chatService.CompressedSessionIds);
        Assert.StartsWith("subagent_", compressedId);
    }

    [Fact(Timeout = 20000)]
    public async Task ExecuteAsync_TokensToCompressDisabled_NoCompression()
    {
        // Same two-turn script: tokens exceed the (disabled) threshold before turn 2,
        // yet compression must never be called when tokensToCompress = 0.
        var orchestrator = CreateOrchestrator(tokensToCompress: 0);
        var toolCall = CreateDelegateTaskCall();

        var result = await orchestrator.ExecuteAsync(
            DelegateTaskArgs,
            toolCall,
            TestContext.Current.CancellationToken);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Completed, branch.Status);
        Assert.True(result.Success);

        Assert.Equal(0, _chatService.CompressCalls);
        Assert.Empty(_chatService.CompressedSessionIds);
    }

    [Fact(Timeout = 20000)]
    public async Task ExecuteAsync_Compression_SetsIsCompressingDuringCallOnly()
    {
        // Compression fires before turn 2, not before turn 1.
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
        Assert.True(_chatService.CompressCalls > 0);
        Assert.True(_chatService.IsCompressingDuringCall);

        // ...and is reset back to false after the call.
        Assert.False(branch.IsCompressing);
    }
}
