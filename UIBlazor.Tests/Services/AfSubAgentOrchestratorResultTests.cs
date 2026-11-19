using System.Runtime.CompilerServices;
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

    /// <summary>
    /// The branch fails on the TRANSPORT (404 from the endpoint), exactly like production.
    /// Neither gateway is substituted here: the REAL <see cref="ChatService"/> runs its own
    /// <see cref="AfCompletionsAdapter"/> against the mock server, so the failure surfaces where
    /// the orchestrator expects it - inside <c>ProcessStreamAsync</c>, from shipping code.
    /// </summary>
    private static AfSubAgentOrchestrator CreateOrchestrator(
        ScriptedChatService chatService,
        IProfileManager profileManager,
        ISystemPromptBuilder promptBuilder,
        IToolManager toolManager,
        IContentFilter contentFilter,
        ICommonSettingsProvider commonSettings,
        IRetryHandler retryHandler)
        => new(
            profileManager,
            promptBuilder,
            toolManager,
            chatService.Service,
            contentFilter,
            new HttpClient(),
            commonSettings,
            retryHandler,
            NullLogger<AfSubAgentOrchestrator>.Instance);

    [Fact]
    public async Task ExecuteAsync_BranchFails_ResultTextReachesToolResult()
    {
        using var chatService = ScriptedChatService.CreateWithRealAdapter(_profileManager.Object.ActiveProfile);
        var orchestrator = CreateOrchestrator(
            chatService,
            _profileManager.Object,
            _promptBuilder.Object,
            _toolManager.Object,
            _contentFilter.Object,
            _commonSettings.Object,
            _retryHandler.Object);
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
}
