using Microsoft.Extensions.Logging.Abstractions;

namespace UIBlazor.Tests.Services;

/// <summary>
/// Retries inside a sub-agent branch: the LLM turn throws on the first attempts and succeeds
/// on the last one. The transport is scripted, but ChatService.ProcessStreamAsync is the REAL
/// one (see <see cref="ScriptedChatService"/>), so the failure surfaces from production code
/// exactly as it does in the app. The branch must complete successfully, the retry
/// handler must be consulted (GetRetryDelay/WaitForRetryAsync), and the final answer must
/// reach the delegate_task result. When the retry budget is exhausted, the last exception
/// escapes the retry loop and the branch fails (previous single-attempt behavior).
/// <para>
/// Only a RETRYABLE failure spends the budget (<see cref="LlmApiException.IsRetryable"/>): a
/// permanent one fails the branch on the first attempt, because a wrong API key or a model that
/// does not exist answers identically every time.
/// </para>
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
        // REAL ChatService, scripted transport: the production ProcessStreamAsync applies the
        // deltas and closes the message, so the assertion covers shipping code.
        using var chatService = ScriptedChatService.Create(Turns.FailFirst(failures: 2));
        var orchestrator = CreateOrchestrator(chatService.Service);
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
        using var chatService = ScriptedChatService.Create(Turns.FailFirst(failures: 10));
        var orchestrator = CreateOrchestrator(chatService.Service);
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
    /// A permanent failure does not spend the retry budget. Before this, a branch with a wrong
    /// API key made three identical attempts with backoff between them: minutes of waiting for an
    /// answer that was knowable on the first attempt, billed against the parent agent's patience.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_PermanentFailure_NotRetried()
    {
        using var chatService = ScriptedChatService.Create(
            Turns.HttpStatus(401, "HttpCode: 401 | invalid api key"));
        var orchestrator = CreateOrchestrator(chatService.Service);
        var toolCall = CreateToolCall();

        var result = await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        // Exactly ONE attempt: the failure is knowable and repeating it changes nothing.
        Assert.Equal(1, chatService.Calls);
        _retryHandler.Verify(
            r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Failed, branch.Status);
        Assert.Contains("401", branch.ErrorMessage);
        Assert.False(result.Success);

        // No retry badge was ever armed for a failure that was not going to be retried.
        Assert.False(branch.IsRetrying);
        Assert.Equal(0, branch.RetryAttempt);
    }

    /// <summary>
    /// A failure that is NOT an <see cref="LlmApiException"/> is a bug in our own code (the
    /// transport wraps everything it produces). Retrying it hides the bug behind minutes of
    /// waiting and makes the stack trace harder to reach.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NonLlmException_NotRetried()
    {
        using var chatService = ScriptedChatService.Create(
            Turns.Throw(new NullReferenceException("Object reference not set")));
        var orchestrator = CreateOrchestrator(chatService.Service);
        var toolCall = CreateToolCall();

        var result = await orchestrator.ExecuteAsync(Args, toolCall, TestContext.Current.CancellationToken);

        Assert.Equal(1, chatService.Calls);
        _retryHandler.Verify(
            r => r.WaitForRetryAsync(It.IsAny<int>(), It.IsAny<Action<int>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var branch = Assert.Single(toolCall.SubAgents!);
        Assert.Equal(SubAgentStatus.Failed, branch.Status);
        Assert.False(result.Success);
    }
}
