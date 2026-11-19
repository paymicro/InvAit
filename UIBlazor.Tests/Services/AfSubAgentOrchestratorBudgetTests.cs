using System.Runtime.CompilerServices;
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
public class AfSubAgentOrchestratorBudgetTests : IDisposable
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
    private ScriptedChatService? _chatService;

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

    public void Dispose() => _chatService?.Dispose();

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

        // REAL ChatService + its own AfCompletionsAdapter against the WireMock endpoint: the
        // branch produces real content and real token accounting (the budget is read from
        // session.TotalTokens), and ProcessStreamAsync is the production one.
        _chatService = ScriptedChatService.CreateWithRealAdapter(
            profileManager: _profileManager.Object,
            commonSettings: _commonSettings.Object);

        return new AfSubAgentOrchestrator(
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _chatService.Service,
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
}
